using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Services.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — startup hook that reconciles the persisted
/// <c>cnas.JobScheduleOverrides</c> table against the running Quartz scheduler.
/// Without this applicator the operator-edited cron expressions would only take
/// effect after a redeploy — the table would say "fire mpay-dispatcher every
/// 10 minutes" but the in-memory scheduler would still carry the baked-in default.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> On <see cref="StartAsync(CancellationToken)"/>, the applicator
/// resolves the Quartz <see cref="ISchedulerFactory"/>, opens a fresh DI scope so the
/// scoped <see cref="IReadOnlyCnasDbContext"/> can be resolved, enumerates every
/// active override row, and for each one:
/// <list type="number">
///   <item>re-schedules the job's cron trigger via
///         <see cref="CronAdminService.ApplyCronToSchedulerAsync"/> (single source of
///         truth shared with the runtime mutation path);</item>
///   <item>if <c>IsPaused</c> = <c>true</c>, calls
///         <c>scheduler.PauseJob(jobKey)</c> so the pause survives restart.</item>
/// </list>
/// Unknown / unregistered job codes are logged at WARN level and skipped — the
/// admin surface guards against this on the write path, but a stale row left
/// behind by a removed job MUST NOT crash startup.
/// </para>
/// <para>
/// <b>Lifetime.</b> The applicator runs once on startup and exits. We register it
/// AFTER <see cref="QuartzComposition.AddCnasJobs"/> registers the scheduler hosted
/// service, but the implementation guards against ordering by resolving the
/// scheduler via the factory rather than caching a reference.
/// </para>
/// <para>
/// <b>Error policy.</b> Any exception raised while applying a single row is logged
/// and swallowed so a corrupt row cannot block the other overrides from being
/// applied. A bulk failure (database unavailable, scheduler crash) is logged at
/// ERROR but the host is allowed to continue starting — the system stays
/// functional with the baked-in defaults until the next admin action.
/// </para>
/// </remarks>
public sealed class JobScheduleApplicator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<JobScheduleApplicator> _logger;

    /// <summary>Constructs the applicator with its scoped DI seam + the scheduler factory.</summary>
    /// <param name="serviceProvider">Root service provider (opens its own scope per run).</param>
    /// <param name="schedulerFactory">Quartz scheduler factory.</param>
    /// <param name="logger">Microsoft.Extensions logger.</param>
    public JobScheduleApplicator(
        IServiceProvider serviceProvider,
        ISchedulerFactory schedulerFactory,
        ILogger<JobScheduleApplicator> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(schedulerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _serviceProvider = serviceProvider;
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobScheduleApplicator failed to reconcile overrides at startup.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Opens a DI scope, enumerates active <see cref="Cnas.Ps.Core.Domain.JobScheduleOverride"/>
    /// rows, and applies each one to the running Quartz scheduler. Errors on a single
    /// row are isolated so one bad override cannot block the rest.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when every applicable row has been processed.</returns>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        var overrides = db.JobScheduleOverrides
            .Where(o => o.IsActive)
            .ToList();

        foreach (var ov in overrides)
        {
            try
            {
                var jobKey = new JobKey(ov.JobCode);
                if (!await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "JobScheduleApplicator: override row references unregistered job {JobCode}; skipping.",
                        ov.JobCode);
                    continue;
                }
                await CronAdminService.ApplyCronToSchedulerAsync(
                    scheduler, ov.JobCode, ov.CronExpression, cancellationToken).ConfigureAwait(false);
                if (ov.IsPaused)
                {
                    await scheduler.PauseJob(jobKey, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "JobScheduleApplicator: failed to apply override for {JobCode}.",
                    ov.JobCode);
            }
        }
    }
}
