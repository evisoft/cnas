using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0204 / TOR CF 20.07-08 — read-only inspector over the running Quartz scheduler.
/// Surfaces every registered job + its currently attached trigger as a
/// <see cref="JobStateDto"/> projection so the admin Jobs dashboard (Blazor
/// <c>JobsDashboard.razor</c>) can show "what is registered, when did it last fire,
/// when does it fire next, is it paused".
/// </summary>
/// <remarks>
/// <para>
/// The inspector is deliberately decoupled from <see cref="IAutomationService"/> — that
/// service is the forward control surface (run-now / re-schedule); this service is the
/// rear-view mirror (what is registered + what is its state). Splitting the two keeps
/// the read path zero-side-effect so the dashboard can poll it cheaply without ever
/// mutating scheduler state.
/// </para>
/// <para>
/// Implemented in Infrastructure (<c>JobStateInspector</c>) over
/// <c>Quartz.ISchedulerFactory</c>. The implementation enumerates every registered
/// <c>JobKey</c>, resolves its triggers, and projects them into the result list ordered
/// alphabetically by job name so the dashboard renders deterministically across reloads.
/// </para>
/// </remarks>
public interface IJobStateInspector
{
    /// <summary>
    /// Returns the current state of every Quartz job + trigger registered with the running
    /// scheduler, alphabetically ordered by <see cref="JobStateDto.JobName"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token plumbed through to the scheduler.</param>
    /// <returns>
    /// <c>Result.Success</c> wrapping the (possibly empty) list of registered jobs when the
    /// scheduler reply is healthy. The implementation surfaces internal Quartz failures as
    /// <see cref="ErrorCodes.Internal"/> rather than propagating exceptions to the controller.
    /// </returns>
    Task<Result<IReadOnlyList<JobStateDto>>> ListAsync(CancellationToken cancellationToken = default);
}
