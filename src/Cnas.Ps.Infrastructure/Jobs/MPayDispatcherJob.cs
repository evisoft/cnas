using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Dispatches outbound MPay benefit payments for <see cref="ServiceApplication"/> rows that
/// are <see cref="ApplicationStatus.Approved"/> with a closed <see cref="Dossier"/> and have
/// not yet been paid. Runs every 5 minutes (see <see cref="QuartzComposition"/>) and is
/// strictly idempotent: a successful run stamps
/// <see cref="ServiceApplication.PaymentDispatchedAtUtc"/>, the upstream MPay transaction
/// id, and the echoed status; subsequent runs skip the row.
/// </summary>
/// <remarks>
/// TOR §2.1, UC14. Failures (network, upstream rejection) leave the row untouched so the
/// next run retries; the upstream MPay service is the source of truth for "did this
/// payment really happen?", which is why we never optimistically stamp on failure.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MPayDispatcherJob(
    ICnasDbContext db,
    IMPayClient mpay,
    ICnasTimeProvider clock,
    IAuditService audit,
    ILogger<MPayDispatcherJob> logger,
    ISqidService sqids) : IJob
{
    /// <summary>
    /// Maximum number of payment rows handled per run. Tuned to keep one tick well below the
    /// 5-minute schedule interval even if every upstream MPay call uses its full timeout.
    /// </summary>
    private const int BatchSize = 100;

    private readonly ICnasDbContext _db = db;
    private readonly IMPayClient _mpay = mpay;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly ILogger<MPayDispatcherJob> _logger = logger;
    private readonly ISqidService _sqids = sqids;

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _clock.UtcNow;
        var ct = context.CancellationToken;

        // Candidates: approved applications whose dossier has been closed (the decision has
        // been emitted) and which have never been dispatched. Join to Solicitant/Dossier so
        // we have everything needed to build the MPay payload without N+1 queries.
        var candidates = await _db.Applications
            .Where(a => a.IsActive
                        && a.Status == ApplicationStatus.Approved
                        && a.PaymentDispatchedAtUtc == null)
            .Join(_db.Dossiers,
                  a => a.Id,
                  d => d.ApplicationId,
                  (a, d) => new { App = a, Dossier = d })
            .Where(x => x.Dossier.IsActive && x.Dossier.ClosedAtUtc != null)
            .Join(_db.Solicitants,
                  x => x.App.SolicitantId,
                  s => s.Id,
                  (x, s) => new { x.App, x.Dossier, Solicitant = s })
            .Take(BatchSize)
            .ToListAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return;
        }

        var dispatched = 0;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Skip silently when prerequisite data is missing. We log a warning so operators
            // can see which dossiers need a back-fill (typically when the upstream
            // amount-computation hasn't yet stored a value on the dossier).
            if (row.Dossier.ComputedAmountMdl is not decimal amount)
            {
                _logger.LogWarning(
                    "MPayDispatcherJob skipping ApplicationId={ApplicationId}: Dossier.ComputedAmountMdl is null.",
                    row.App.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Solicitant.BankIban))
            {
                _logger.LogWarning(
                    "MPayDispatcherJob skipping ApplicationId={ApplicationId}: Solicitant.BankIban is missing.",
                    row.App.Id);
                continue;
            }

            var reference = $"{row.App.ReferenceNumber ?? row.App.Id.ToString(CultureInfo.InvariantCulture)}-{row.Dossier.DossierNumber}";
            var payload = new MPayOutbound(
                BeneficiaryIdnp: row.Solicitant.NationalId,
                BeneficiaryIban: row.Solicitant.BankIban!,
                AmountMdl: amount,
                Reference: reference);

            var result = await _mpay.SendAsync(payload, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MPayDispatcherJob: MPay dispatch failed for ApplicationId={ApplicationId} ({ErrorCode}: {ErrorMessage}). Row left for retry.",
                    row.App.Id, result.ErrorCode, result.ErrorMessage);
                continue;
            }

            // Last-write-wins stamp. The select-then-update lives inside the SAME tracking
            // context, so the next SaveChanges flushes everything atomically.
            row.App.PaymentDispatchedAtUtc = now;
            row.App.PaymentTransactionId = result.Value.TransactionId;
            row.App.PaymentStatus = result.Value.Status;
            row.App.UpdatedAtUtc = now;

            // Critical-severity audit so MLog mirrors it (SEC 056). Includes the sqid of the
            // application so log readers can correlate without exposing raw db keys downstream.
            var details = JsonSerializer.Serialize(new
            {
                applicationSqid = _sqids.Encode(row.App.Id),
                dossierNumber = row.Dossier.DossierNumber,
                amountMdl = amount,
                transactionId = result.Value.TransactionId,
                status = result.Value.Status,
            });
            await _audit.RecordAsync(
                eventCode: "PAYMENT.DISPATCHED",
                severity: AuditSeverity.Critical,
                actorId: "system:MPayDispatcherJob",
                targetEntity: nameof(ServiceApplication),
                targetEntityId: row.App.Id,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                cancellationToken: ct).ConfigureAwait(false);

            dispatched++;
        }

        if (dispatched > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "MPayDispatcherJob dispatched {Count} payments at {NowUtc:o}.", dispatched, now);
        }
    }
}
