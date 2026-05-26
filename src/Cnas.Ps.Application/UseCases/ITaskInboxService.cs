using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC05 / R0127 / CF 16.11 — Execute tasks, plus per-task delegation. Workflow
/// inbox for the calling user; also the dispatch seam for per-task reassignment used by
/// the admin REST surface and the <c>UserAbsenceLifecycleJob</c>.</summary>
public interface ITaskInboxService
{
    /// <summary>Lists tasks assigned to the calling user (or their groups) with paging.</summary>
    Task<Result<PagedResult<TaskInboxItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Claims a task assigned to a group inbox.</summary>
    Task<Result> ClaimAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Completes the task with a result payload (workflow continues).</summary>
    Task<Result> CompleteAsync(string taskId, string resultJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0127 / CF 16.11 — Reassigns the supplied task to a new assignee. On first
    /// reassignment, the CURRENT assignee is captured on
    /// <c>WorkflowTask.OriginalAssigneeUserId</c> so a subsequent revert can restore
    /// the original owner. The reason is persisted on the task AND on the
    /// <c>WORKFLOWTASK.REASSIGNED</c> audit row.
    /// </summary>
    /// <param name="taskId">Internal <c>WorkflowTask.Id</c>.</param>
    /// <param name="newAssigneeUserId">Internal <c>UserProfile.Id</c> of the new assignee.</param>
    /// <param name="reason">Free-text justification, 3..500 chars.</param>
    /// <param name="absenceId">
    /// Optional FK to the <c>UserAbsence</c> driving this reassignment. <c>null</c> when
    /// the operator triggered the reassignment by hand; populated by
    /// <c>IUserAbsenceService.ActivateAsync</c> when the route is absence-driven.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Sqid-encoded snapshot of the updated task on success;
    /// <see cref="ErrorCodes.NotFound"/> when the id does not match an active task;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the task is in a terminal status;
    /// <see cref="ErrorCodes.Forbidden"/> when the new assignee is not active.
    /// </returns>
    Task<Result<WorkflowTaskOutputDto>> ReassignAsync(
        long taskId,
        long newAssigneeUserId,
        string reason,
        long? absenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0127 / CF 16.11 — Reverts a previously reassigned task back to its
    /// <c>OriginalAssigneeUserId</c>. Increments <c>ReassignmentCount</c>, clears the
    /// <c>DelegatedFromAbsenceId</c> link, and emits a
    /// <c>WORKFLOWTASK.REASSIGNMENT_REVERTED</c> audit row.
    /// </summary>
    /// <param name="taskId">Internal <c>WorkflowTask.Id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; <see cref="ErrorCodes.NotFound"/> when
    /// the id does not match an active task; <see cref="ErrorCodes.ValidationFailed"/>
    /// when the task is in a terminal status OR was never reassigned.
    /// </returns>
    Task<Result> RevertReassignmentAsync(long taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0381 / UC05 — supervisor workspace view. Lists active (non-terminal) tasks
    /// assigned to ANY user that shares a <c>UserGroupMembership</c> with the calling
    /// supervisor, excluding the supervisor's own work. Backs the
    /// <c>GET /api/tasks/supervisor/team</c> surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Team" definition.</b> A user is in the supervisor's team when the supervisor
    /// is a direct <see cref="Cnas.Ps.Core.Domain.UserGroupMembership"/> member of a
    /// <see cref="Cnas.Ps.Core.Domain.UserGroup"/> the user also belongs to (R2270 /
    /// SEC 023). Transitive group nesting is NOT walked here — this is the MVP view;
    /// future iterations can pivot to <c>IUserGroupRoleResolver.ResolveDescendantsAsync</c>
    /// when supervisors need cross-office visibility.
    /// </para>
    /// <para>
    /// <b>Read path.</b> The query routes through <c>IReadOnlyCnasDbContext</c> so the
    /// team-queue listing lands on the streaming-replication replica (R0026 /
    /// TOR PSR 006) — supervisors browsing the queue must not contend with the writer
    /// processing workflow events.
    /// </para>
    /// </remarks>
    /// <param name="assigneeFilterSqid">
    /// Optional Sqid filter — when supplied, only tasks assigned to the matching user
    /// are returned. Malformed Sqids surface as <see cref="ErrorCodes.InvalidSqid"/>.
    /// </param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page; clamped server-side to <c>[1, 50]</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// 200 OK with a paged list of <see cref="SupervisorTeamTaskDto"/> on success;
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the assignee filter is malformed.
    /// </returns>
    Task<Result<PagedResult<SupervisorTeamTaskDto>>> ListTeamQueueAsync(
        string? assigneeFilterSqid,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0381 / CF 16.11 — supervisor-driven task reassignment. Sqid-typed convenience
    /// wrapper around <see cref="ReassignAsync(long, long, string, long?, CancellationToken)"/>
    /// — decodes the task + new-assignee Sqids, delegates to the long-typed overload,
    /// and propagates the same audit + observability side-effects. Emits the
    /// <c>cnas.task_reassign.total</c> counter tagged with <c>reason_bucket</c>
    /// (<c>short</c> for reasons ≤30 chars, <c>long</c> otherwise — bounded cardinality).
    /// </summary>
    /// <param name="taskSqid">Sqid-encoded workflow-task id.</param>
    /// <param name="newAssigneeSqid">Sqid-encoded id of the user receiving the task.</param>
    /// <param name="reason">Free-text justification, 3..500 chars.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Sqid-encoded snapshot of the updated task on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed ids;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the reason fails the
    /// 3..500-chars envelope or the task is terminal;
    /// <see cref="ErrorCodes.NotFound"/> when the task or assignee is missing.
    /// </returns>
    Task<Result<WorkflowTaskOutputDto>> ReassignTaskAsync(
        string taskSqid,
        string newAssigneeSqid,
        string reason,
        CancellationToken cancellationToken = default);
}
