using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.WorkflowTasks;

/// <summary>
/// R0127 / CF 16.11 — admin service for operator-declared user-absence windows. Plans
/// an absence, activates it on the scheduled day, routes the absent user's open tasks
/// to a delegate for the duration, and reverts unprocessed tasks on completion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> <c>Planned → Active → Completed</c> is the happy path; the
/// <c>UserAbsenceLifecycleJob</c> drives the transitions automatically on schedule.
/// <c>Planned → Cancelled</c> is the only escape — once a row is <c>Active</c> the
/// only way out is Completion so the revert sweep runs and the original assignees
/// recover their tasks.
/// </para>
/// <para>
/// <b>Atomicity.</b> Activation and completion each run inside a single
/// <c>SaveChangesAsync</c> call so a partial failure does not leave half-routed tasks
/// in production.
/// </para>
/// </remarks>
public interface IUserAbsenceService
{
    /// <summary>
    /// Plans a new absence for the supplied user with a delegate. Validates: start ≤ end,
    /// delegate ≠ user, no overlapping <c>Planned</c> or <c>Active</c> row for the same
    /// user.
    /// </summary>
    /// <param name="input">Plan payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Sqid-encoded snapshot on success; <see cref="ErrorCodes.ValidationFailed"/> when
    /// the validator rejects the payload; <see cref="ErrorCodes.NotFound"/> when either
    /// user Sqid does not match an active <c>UserProfile</c>.
    /// </returns>
    Task<Result<UserAbsenceOutputDto>> PlanAsync(UserAbsenceCreateDto input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips a <c>Planned</c> row to <c>Active</c>, routes every open task assigned to
    /// the absent user (Status ∈ Pending, InProgress, Overdue) to the nominated
    /// delegate, stamps <c>DelegatedFromAbsenceId</c> on each routed row, and
    /// increments <c>RoutedTaskCount</c>. Idempotent — re-activating an already-active
    /// row is a no-op success.
    /// </summary>
    /// <param name="absenceId">Internal <c>UserAbsence.Id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; <see cref="ErrorCodes.NotFound"/> when
    /// the absence id is unknown; <see cref="ErrorCodes.ValidationFailed"/> when the
    /// row is in a terminal status (<c>Completed</c> or <c>Cancelled</c>).
    /// </returns>
    Task<Result> ActivateAsync(long absenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips an <c>Active</c> row to <c>Completed</c> and reverts every still-open task
    /// whose <c>DelegatedFromAbsenceId</c> matches this row back to its
    /// <c>OriginalAssigneeUserId</c>. Tasks already touched by the delegate (different
    /// assignee, completed, etc.) are left as-is.
    /// </summary>
    /// <param name="absenceId">Internal <c>UserAbsence.Id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; <see cref="ErrorCodes.NotFound"/> when
    /// the absence id is unknown; <see cref="ErrorCodes.ValidationFailed"/> when the
    /// row is not in <c>Active</c>.
    /// </returns>
    Task<Result> CompleteAsync(long absenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips a <c>Planned</c> row to <c>Cancelled</c>. Rejects when the row is already
    /// <c>Active</c> (must Complete instead so the revert sweep runs),
    /// <c>Completed</c>, or already <c>Cancelled</c>.
    /// </summary>
    /// <param name="absenceId">Internal <c>UserAbsence.Id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> CancelAsync(long absenceId, CancellationToken cancellationToken = default);

    /// <summary>Fetches an absence by id, or null when not found / soft-deleted.</summary>
    /// <param name="absenceId">Internal <c>UserAbsence.Id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserAbsenceOutputDto?> GetAsync(long absenceId, CancellationToken cancellationToken = default);

    /// <summary>Lists every active (non-soft-deleted) absence row for a user, newest first.</summary>
    /// <param name="userId">Internal <c>UserProfile.Id</c> of the absent user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<UserAbsenceOutputDto>> ListForUserAsync(long userId, CancellationToken cancellationToken = default);
}
