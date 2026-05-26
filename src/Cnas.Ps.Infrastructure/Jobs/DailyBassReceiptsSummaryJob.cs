using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0818 / TOR BP 1.2-I — Quartz job that runs daily at 23:55 UTC and writes
/// a single Information-severity audit row summarising the outcomes of every
/// <see cref="TreasuryPaymentReceipt"/> processed during the operating day.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data source.</b> The job groups all
/// <see cref="TreasuryPaymentReceipt"/> rows whose
/// <see cref="TreasuryPaymentReceipt.DistributedAtUtc"/> falls within the
/// running calendar day (00:00 → 23:59:59 UTC) by their
/// <see cref="TreasuryPaymentReceipt.DistributionStatus"/> and writes the
/// per-status counts into the audit detail payload.
/// </para>
/// <para>
/// <b>Complementary cadence.</b> This job is the daily-rollup companion to
/// <see cref="TreasuryDistributionJob"/> (which fires every 15 minutes). The
/// 15-minute job mutates the rows; this job summarises them.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> is belt-and-braces —
/// the underlying group-by query is read-only so parallel fires would just
/// waste DB round-trips.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DailyBassReceiptsSummaryJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "daily-bass-receipts-summary";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "daily-bass-receipts-summary-trigger";

    /// <summary>Cron expression — daily at 23:55 UTC.</summary>
    public const string Cron = "0 55 23 * * ?";

    /// <summary>Stable audit event code emitted on every fire.</summary>
    public const string AuditEventCode = "BASS_RECEIPTS.DAILY_SUMMARY";

    /// <summary>Stable actor id stamped on the audit row.</summary>
    public const string SystemActor = "system:daily-bass-summary";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.DailyBassReceiptsSummary;

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<DailyBassReceiptsSummaryJob> _logger;

    /// <summary>Constructs the job with its scoped collaborators.</summary>
    /// <param name="db">Application DbContext (read-only here).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public DailyBassReceiptsSummaryJob(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IAuditService audit,
        IPeakHourGate peakHourGate,
        ILogger<DailyBassReceiptsSummaryJob> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _audit = audit;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile keeps the
        // daily summary inside the off-peak window. The cron is already 23:55
        // UTC so the gate is belt-and-braces for emergency manual fires.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        var now = _clock.UtcNow;
        var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        var perStatus = await _db.TreasuryPaymentReceipts
            .Where(r => r.IsActive
                && r.DistributedAtUtc != null
                && r.DistributedAtUtc >= dayStart
                && r.DistributedAtUtc < dayEnd)
            .GroupBy(r => r.DistributionStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        int totalCount = perStatus.Sum(g => g.Count);
        int distributed = perStatus.Where(g => g.Status == TreasuryPaymentDistributionStatus.Distributed).Sum(g => g.Count);
        int partial = perStatus.Where(g => g.Status == TreasuryPaymentDistributionStatus.PartiallyDistributed).Sum(g => g.Count);
        int failed = perStatus.Where(g => g.Status == TreasuryPaymentDistributionStatus.Failed).Sum(g => g.Count);
        int pending = perStatus.Where(g => g.Status == TreasuryPaymentDistributionStatus.Pending).Sum(g => g.Count);

        var details = JsonSerializer.Serialize(new
        {
            dayStartUtc = dayStart.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            dayEndUtc = dayEnd.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            totalCount,
            distributed,
            partiallyDistributed = partial,
            failed,
            pending,
        });

        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: SystemActor,
            targetEntity: nameof(TreasuryPaymentReceipt),
            targetEntityId: null,
            detailsJson: details,
            sourceIp: null,
            correlationId: context.FireInstanceId,
            cancellationToken: ct).ConfigureAwait(false);

        CnasMeter.BassDailySummary.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "executed"));

        _logger.LogInformation(
            "DailyBassReceiptsSummaryJob completed: total={Total} distributed={Distributed} partial={Partial} failed={Failed} pending={Pending}",
            totalCount, distributed, partial, failed, pending);
    }
}
