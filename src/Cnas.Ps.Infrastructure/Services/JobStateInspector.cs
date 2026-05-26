using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0204 / TOR CF 20.07-08 — concrete <see cref="IJobStateInspector"/> that enumerates
/// every Quartz job + attached trigger registered with the running scheduler and
/// projects them into <see cref="JobStateDto"/> rows for the admin dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Registered as a <c>Singleton</c> in DI because the implementation is
/// stateless and the underlying <see cref="ISchedulerFactory"/> is itself a singleton.
/// The inspector never mutates scheduler state — every method only queries — so
/// concurrent calls are safe.
/// </para>
/// <para>
/// <b>Error policy.</b> Quartz exceptions raised while walking the scheduler are caught
/// and surfaced as <see cref="ErrorCodes.Internal"/>-coded failures so the controller can
/// return a 500 ProblemDetails without leaking framework details. The exception is logged
/// at <c>Warning</c> level with the failing job key (if any) so operators can correlate.
/// </para>
/// </remarks>
/// <param name="schedulerFactory">Quartz scheduler factory — resolved fresh per call to avoid caching shutdown state.</param>
/// <param name="logger">Microsoft.Extensions logger.</param>
public sealed class JobStateInspector(
    ISchedulerFactory schedulerFactory,
    ILogger<JobStateInspector> logger) : IJobStateInspector
{
    private readonly ISchedulerFactory _schedulerFactory = schedulerFactory;
    private readonly ILogger<JobStateInspector> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<JobStateDto>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var jobKeys = await scheduler
                .GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<JobStateDto>(capacity: jobKeys.Count);
            foreach (var key in jobKeys.OrderBy(k => k.Name, StringComparer.Ordinal))
            {
                var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken).ConfigureAwait(false);
                if (triggers.Count == 0)
                {
                    // Durable jobs without an attached trigger still surface — operators
                    // need to see them so they can attach a trigger via the admin tooling.
                    rows.Add(new JobStateDto(
                        JobName: key.Name,
                        JobGroup: key.Group,
                        TriggerName: string.Empty,
                        NextFireUtc: null,
                        LastFireUtc: null,
                        State: "None"));
                    continue;
                }

                // A single Quartz JobKey can theoretically have multiple triggers attached.
                // For the dashboard we project one row per (job, trigger) pair so the
                // operator can see each schedule independently.
                foreach (var trigger in triggers)
                {
                    var state = await scheduler.GetTriggerState(trigger.Key, cancellationToken).ConfigureAwait(false);
                    rows.Add(new JobStateDto(
                        JobName: key.Name,
                        JobGroup: key.Group,
                        TriggerName: trigger.Key.Name,
                        NextFireUtc: trigger.GetNextFireTimeUtc()?.UtcDateTime,
                        LastFireUtc: trigger.GetPreviousFireTimeUtc()?.UtcDateTime,
                        State: state.ToString()));
                }
            }

            return Result<IReadOnlyList<JobStateDto>>.Success(rows);
        }
        catch (SchedulerException ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Quartz scheduler state.");
            return Result<IReadOnlyList<JobStateDto>>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }
}
