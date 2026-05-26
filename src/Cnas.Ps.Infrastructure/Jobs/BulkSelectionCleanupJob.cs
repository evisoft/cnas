using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — Quartz job that hard-deletes expired
/// <c>BulkSelection</c> rows past the grace window. Selections expire 1 hour after
/// creation (operational lifetime); this job sweeps once per day and removes rows
/// whose <c>ExpiresAtUtc</c> is older than the wider <c>CleanupGraceDays</c> (default
/// 7 days). Consumed-but-not-yet-expired rows are also removed once past the grace
/// window so the table doesn't accumulate single-use handles indefinitely.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cadence.</b> Runs once per day at <c>03:15</c> UTC — well after the audit
/// archive replay window so the two jobs do not compete for DB connections.
/// </para>
/// <para>
/// <b>Soft-vs-hard delete.</b> Selection rows are a transient mechanism, not
/// business-meaningful state, so the cleanup performs a hard delete rather than
/// flipping <c>IsActive = false</c>. A future investigator who needs the filter
/// envelope of a long-past run can still recover it via the audit trail —
/// <c>BulkOperationRun</c> rows are kept forever and carry the <c>BulkSelectionId</c>.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> prevents an overlapping fire
/// from racing the same rows; the underlying delete is idempotent regardless.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class BulkSelectionCleanupJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration / lookups.</summary>
    public const string JobIdentity = "bulk-selection-cleanup";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "bulk-selection-cleanup-trigger";

    /// <summary>Cron expression — every day at 03:15 UTC (server time).</summary>
    public const string Cron = "0 15 3 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.BulkSelectionCleanup;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly IPeakHourGate _peakHourGate;
    private readonly BulkSelectionOptions _opts;
    private readonly ILogger<BulkSelectionCleanupJob> _logger;

    /// <summary>Constructs the cleanup job with its collaborators.</summary>
    /// <param name="scopes">DI scope factory used to resolve the scoped DbContext per fire.</param>
    /// <param name="clock">UTC clock used to compute the cut-off.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="options">Bound bulk-selection options.</param>
    /// <param name="logger">Structured logger.</param>
    public BulkSelectionCleanupJob(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        IPeakHourGate peakHourGate,
        IOptions<BulkSelectionOptions> options,
        ILogger<BulkSelectionCleanupJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _clock = clock;
        _peakHourGate = peakHourGate;
        _opts = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile keeps the
        // sweep inside the off-peak window. The cron is already 03:15 UTC so
        // the gate is belt-and-braces for emergency manual fires.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();

        var cutoff = _clock.UtcNow - TimeSpan.FromDays(_opts.CleanupGraceDays);
        var stale = await db.BulkSelections
            .Where(s => s.ExpiresAtUtc <= cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return;
        }
        foreach (var row in stale)
        {
            db.BulkSelections.Remove(row);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "BulkSelectionCleanupJob removed {Count} expired bulk-selection rows past the {Days}-day grace window.",
            stale.Count, _opts.CleanupGraceDays);
    }
}
