using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0127 / CF 16.11 — Quartz job that drives the <see cref="UserAbsence"/> lifecycle.
/// Runs every 5 minutes; for every row whose <c>StartDateUtc</c> has been reached the
/// job activates the absence (routes the absent user's open tasks to the delegate),
/// and for every <c>Active</c> row past <c>EndDateUtc</c> it completes the absence
/// (reverts still-open delegated tasks to their original owner).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> Activation and completion mutate the row's <c>Status</c> column
/// as part of the same SaveChanges call that touches the task graph; the predicate the
/// job uses excludes already-flipped rows so a second fire on the same data set is a
/// no-op. Mirrors the discipline used by <c>UnclaimedTaskEscalationJob</c>.
/// </para>
/// <para>
/// <b>Bounded work.</b> No explicit batch cap today — the active absence working set
/// is tiny (operator-driven, dozens at most). If volume grows the predicate can be
/// trimmed by an <c>OrderBy</c> + <c>Take</c> exactly like the unclaimed-task job.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class UserAbsenceLifecycleJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "user-absence-lifecycle";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "user-absence-lifecycle-trigger";

    /// <summary>
    /// Cron expression — every 5 minutes on the second-zero boundary. Chosen to match
    /// the cadence of <c>MPayDispatcherJob</c> so operator-driven absences activate
    /// promptly without flooding the scheduler.
    /// </summary>
    public const string Cron = "0 0/5 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Anytime profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.UserAbsenceLifecycle;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<UserAbsenceLifecycleJob> _logger;

    /// <summary>Constructs the lifecycle job with its collaborators.</summary>
    /// <param name="scopes">Scope factory — the job resolves scoped collaborators per fire.</param>
    /// <param name="clock">UTC clock — drives the activation / completion cut-off.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    public UserAbsenceLifecycleJob(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        IPeakHourGate peakHourGate,
        ILogger<UserAbsenceLifecycleJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _clock = clock;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. Anytime profile means the gate
        // always allows; the uniform call keeps the counter time-series complete.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        var now = _clock.UtcNow;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserAbsenceService>();

        // ─── 1. Activate planned rows whose start date has been reached. ───
        var planned = await db.UserAbsences
            .Where(a => a.IsActive
                && a.Status == UserAbsenceStatus.Planned
                && a.StartDateUtc <= now)
            .Select(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var activated = 0;
        foreach (var id in planned)
        {
            var outcome = await service.ActivateAsync(id, ct).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                activated += 1;
            }
            else
            {
                _logger.LogWarning(
                    "UserAbsenceLifecycleJob failed to activate absence {AbsenceId}: {ErrorCode} {Message}",
                    id, outcome.ErrorCode, outcome.ErrorMessage);
            }
        }
        if (activated > 0)
        {
            CnasMeter.UserAbsenceActivated.Add(activated);
        }

        // ─── 2. Complete active rows whose end date has elapsed. ───
        var due = await db.UserAbsences
            .Where(a => a.IsActive
                && a.Status == UserAbsenceStatus.Active
                && a.EndDateUtc < now)
            .Select(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var completed = 0;
        foreach (var id in due)
        {
            var outcome = await service.CompleteAsync(id, ct).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                completed += 1;
            }
            else
            {
                _logger.LogWarning(
                    "UserAbsenceLifecycleJob failed to complete absence {AbsenceId}: {ErrorCode} {Message}",
                    id, outcome.ErrorCode, outcome.ErrorMessage);
            }
        }
        if (completed > 0)
        {
            CnasMeter.UserAbsenceCompleted.Add(completed);
        }

        if (activated + completed > 0)
        {
            _logger.LogInformation(
                "UserAbsenceLifecycleJob processed activations={Activated} completions={Completed}",
                activated, completed);
        }
    }
}
