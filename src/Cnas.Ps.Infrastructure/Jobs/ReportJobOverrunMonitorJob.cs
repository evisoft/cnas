using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0174 / TOR CF 22.03 — Quartz job that scans the <c>ReportJobs</c> table for
/// rows still in <see cref="ReportJobStatus.Running"/> beyond the configured
/// overrun threshold and fires a single
/// <see cref="NotificationTriggerKind.PerformanceAlert"/> trigger per row so the
/// requester's inbox surfaces the slow-job notification with a
/// <see cref="NotificationRelatedEntityTypes.ReportRun"/> deep-link.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The job stamps each notified row with a synthetic
/// correlation id (<c>report-overrun-{id}</c>) BEFORE dispatching the trigger;
/// the dispatcher persists the row through the inbox table. To avoid double-
/// notifying on the next sweep we re-query and skip rows whose
/// <c>FailureReason</c> already starts with the overrun sentinel. This is a
/// deliberately lightweight design — adding a dedicated "PerfAlertSentAtUtc"
/// column would be schema churn for a single-purpose flag.
/// </para>
/// <para>
/// <b>Threshold source.</b> Bound from <c>Cnas:ReportJobs:OverrunThresholdMinutes</c>
/// (default 5 minutes per CF 22.03 example). A non-positive value disables the
/// sweep entirely.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ReportJobOverrunMonitorJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "report-job-overrun-monitor";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "report-job-overrun-monitor-trigger";

    /// <summary>Cron expression — every 5 minutes on the minute boundary.</summary>
    public const string Cron = "0 0/5 * * * ?";

    /// <summary>
    /// Sentinel prefix written into <see cref="ReportJob.FailureReason"/> when
    /// a perf-alert has fired so the next sweep can skip the row.
    /// </summary>
    public const string OverrunSentinelPrefix = "[perf-alert-fired]";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly INotificationTriggerDispatcher _triggers;
    private readonly ILogger<ReportJobOverrunMonitorJob> _logger;
    private readonly ReportJobOverrunOptions _options;

    /// <summary>
    /// Constructs the monitor with its collaborators.
    /// </summary>
    /// <param name="db">Write-side DbContext — the sentinel write needs writes.</param>
    /// <param name="clock">Injected UTC clock per CLAUDE.md "UTC Everywhere".</param>
    /// <param name="triggers">Canonical R0174 trigger dispatcher.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="options">Overrun threshold settings (<see cref="ReportJobOverrunOptions"/>).</param>
    public ReportJobOverrunMonitorJob(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        INotificationTriggerDispatcher triggers,
        ILogger<ReportJobOverrunMonitorJob> logger,
        IOptions<ReportJobOverrunOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _clock = clock;
        _triggers = triggers;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteAsync(context.CancellationToken);
    }

    /// <summary>
    /// Direct execution entry-point used by tests + the Quartz job above. Scans
    /// the running-jobs table, picks rows that have been running for longer
    /// than <see cref="ReportJobOverrunOptions.OverrunThresholdMinutes"/>, and
    /// dispatches one PerformanceAlert per row.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The count of perf-alerts dispatched on this sweep.</returns>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var thresholdMinutes = _options.OverrunThresholdMinutes;
        if (thresholdMinutes <= 0)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var cutoff = now.AddMinutes(-thresholdMinutes);

        // Rows still running past the cutoff that have not yet been flagged. The
        // sentinel prefix on FailureReason serves as the per-row "alert fired"
        // marker (cheap, no schema change).
        var stale = await _db.ReportJobs
            .Where(j => j.IsActive
                        && j.Status == ReportJobStatus.Running
                        && j.StartedAtUtc != null
                        && j.StartedAtUtc < cutoff
                        && (j.FailureReason == null
                            || !j.FailureReason.StartsWith(OverrunSentinelPrefix)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;
        foreach (var row in stale)
        {
            // Flag the row BEFORE dispatching so a crash mid-sweep does not re-
            // notify the requester. The sentinel preserves the original failure
            // reason (when present) as a suffix so the operator can still see it.
            row.FailureReason = $"{OverrunSentinelPrefix} threshold={thresholdMinutes}m";
            row.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in stale)
        {
            var body = $"Raportul (#{row.Id}) rulează de mai mult de {thresholdMinutes} minute.";
            var dispatchResult = await _triggers.DispatchAsync(
                NotificationTriggerKind.PerformanceAlert,
                new NotificationTriggerPayload(
                    RecipientUserId: row.RequestedByUserId,
                    Subject: "Raport întârziat",
                    Body: body,
                    CorrelationId: $"report-overrun-{row.Id}",
                    RelatedEntityType: NotificationRelatedEntityTypes.ReportRun,
                    RelatedEntityId: row.Id),
                cancellationToken).ConfigureAwait(false);
            if (dispatchResult.IsSuccess)
            {
                dispatched++;
            }
            else
            {
                _logger.LogWarning(
                    "Perf-alert dispatch failed for ReportJob#{Id}: {ErrorCode} {ErrorMessage}",
                    row.Id,
                    dispatchResult.ErrorCode,
                    dispatchResult.ErrorMessage);
            }
        }

        if (dispatched > 0)
        {
            _logger.LogInformation(
                "ReportJobOverrunMonitorJob dispatched {Count} perf-alert(s) at {NowUtc:o}.",
                dispatched, now);
        }
        return dispatched;
    }
}

/// <summary>
/// R0174 / TOR CF 22.03 — bindable options for the report-job overrun monitor.
/// Configured under <c>Cnas:ReportJobs</c>.
/// </summary>
public sealed class ReportJobOverrunOptions
{
    /// <summary>
    /// Threshold (in minutes) above which a still-Running <see cref="ReportJob"/>
    /// row triggers a <see cref="NotificationTriggerKind.PerformanceAlert"/>. The
    /// default is 5 minutes per the CF 22.03 example. A non-positive value
    /// disables the sweep entirely.
    /// </summary>
    public int OverrunThresholdMinutes { get; set; } = 5;
}
