using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.Treasury;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0911 / TOR BP 2.2-B — Quartz job that drains the
/// <c>TreasuryPaymentReceipt</c> backlog every 15 minutes. For each row in the
/// <see cref="TreasuryPaymentDistributionStatus.Pending"/> state the job calls
/// <see cref="ITreasuryPaymentService.DistributeAsync"/> which projects the
/// matching REV-5 rows into <c>PersonalAccountEntry</c>. Per-receipt outcomes
/// are counted on the <c>cnas.treasury.distributed{outcome}</c> meter inside
/// the service; this job only emits a per-fire summary log line.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> is belt-and-braces — the
/// distribution path is idempotent (a non-Pending receipt is rejected with
/// the stable <c>ALREADY_DISTRIBUTED</c> message), but parallel fires would
/// only re-query the same Pending set and waste DB round-trips.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class TreasuryDistributionJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "treasury-distribution";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "treasury-distribution-trigger";

    /// <summary>Cron expression — every 15 minutes on the quarter hour.</summary>
    public const string Cron = "0 0/15 * * * ?";

    /// <summary>
    /// Maximum receipts drained per fire. Bounds the per-fire database work so
    /// the scheduler can complete within the 15-minute cadence even if a
    /// backlog builds up; the next fire will continue draining the queue.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Anytime profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.TreasuryDistribution;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<TreasuryDistributionJob> _logger;

    /// <summary>Constructs the job with its scope factory + logger dependencies.</summary>
    /// <param name="scopes">DI scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public TreasuryDistributionJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<TreasuryDistributionJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. Anytime profile means the gate
        // always allows; we still call it so the counter increments uniformly
        // for every job and the global override toggle is honoured.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITreasuryPaymentService>();

        // FIFO drain — oldest receipts first so older payments are credited
        // before newer ones (matches the natural-ordering invariant operators
        // expect when reconciling against the Treasury feed).
        var pendingIds = await db.TreasuryPaymentReceipts
            .Where(r => r.IsActive && r.DistributionStatus == TreasuryPaymentDistributionStatus.Pending)
            .OrderBy(r => r.ReceiptDate)
            .ThenBy(r => r.Id)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (pendingIds.Count == 0)
        {
            _logger.LogDebug("TreasuryDistributionJob — no Pending receipts to drain.");
            return;
        }

        int successCount = 0;
        int failureCount = 0;
        foreach (var id in pendingIds)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var result = await service.DistributeAsync(id, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                    _logger.LogWarning(
                        "TreasuryDistributionJob — DistributeAsync({ReceiptId}) returned {Code}: {Message}",
                        id, result.ErrorCode, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(ex, "TreasuryDistributionJob — DistributeAsync({ReceiptId}) threw.", id);
            }
        }

        _logger.LogInformation(
            "TreasuryDistributionJob completed: drained={Drained} success={Success} failure={Failure}",
            pendingIds.Count, successCount, failureCount);
    }
}
