using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Workflow;

/// <summary>
/// R0125 / CF 16.09 — service contract for the workflow-task step-history projection.
/// Two responsibilities: a writer-side <see cref="RecordEventAsync"/> called from every
/// site that mutates a <see cref="WorkflowTask"/>, and a reader-side
/// <see cref="GetHistoryAsync"/> consumed by the admin REST surface to render the
/// per-task timeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write path is idempotent at the event level.</b> Every call to
/// <see cref="RecordEventAsync"/> inserts a new row — the projection is append-only
/// and deduplicates by surrogate-key sequence, not by content. Callers SHOULD invoke it
/// exactly once per state transition; double-invocation is not silently coalesced.
/// </para>
/// <para>
/// <b>Read path is workflow-scoped.</b> <see cref="GetHistoryAsync"/> takes a Sqid-encoded
/// task id (the public addressable handle) and returns a paged response. Filter
/// validation lives in <c>WorkflowTaskHistoryFilterDtoValidator</c>; the service
/// re-applies the bounds defensively so a hand-crafted call cannot bypass them.
/// </para>
/// </remarks>
public interface IWorkflowTaskHistoryService
{
    /// <summary>
    /// Records one lifecycle event on the supplied task. Inserts a new
    /// <see cref="WorkflowTaskStepHistory"/> row stamped with
    /// <c>ICnasTimeProvider.UtcNow</c>; the actor is the supplied user id (null for
    /// system events). Emits a Counter increment on
    /// <c>CnasMeter.WorkflowTaskHistoryEvent</c> tagged with the event kind, and an
    /// Information-severity <c>WORKFLOW_TASK.HISTORY_RECORDED</c> audit row.
    /// </summary>
    /// <param name="workflowTaskId">Internal <see cref="AuditableEntity.Id"/> of the target <see cref="WorkflowTask"/>.</param>
    /// <param name="eventKind">The transition kind being recorded.</param>
    /// <param name="stepCode">
    /// Stable step code the task is in / transitioning into (≤ 64 chars, non-empty).
    /// </param>
    /// <param name="actorUserId">
    /// Internal <c>UserProfile.Id</c> of the actor that triggered the event;
    /// <c>null</c> for system-generated events (SLA sweep, lifecycle job).
    /// </param>
    /// <param name="decisionCode">
    /// Stable decision code for <see cref="WorkflowTaskStepEventKind.Exited"/> events;
    /// <c>null</c> otherwise. ≤ 64 chars.
    /// </param>
    /// <param name="note">
    /// Optional free-text descriptive note (≤ 1000 chars).
    /// </param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on persist;
    /// <see cref="ErrorCodes.ValidationFailed"/> when <paramref name="stepCode"/> is
    /// empty or exceeds 64 chars;
    /// <see cref="ErrorCodes.WorkflowTaskHistoryFailed"/> on persist failure.
    /// </returns>
    System.Threading.Tasks.Task<Result> RecordEventAsync(
        long workflowTaskId,
        WorkflowTaskStepEventKind eventKind,
        string stepCode,
        long? actorUserId,
        string? decisionCode,
        string? note,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged slice of history rows for the task identified by
    /// <paramref name="workflowTaskSqid"/>, ordered ascending by
    /// <c>OccurredAt</c>. Optionally filtered by event kind (see
    /// <see cref="WorkflowTaskHistoryFilterDto.EventKind"/>).
    /// </summary>
    /// <param name="workflowTaskSqid">Sqid-encoded <see cref="AuditableEntity.Id"/> of the target <see cref="WorkflowTask"/>.</param>
    /// <param name="filter">Optional event-kind + paging filter.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> with the paged response on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the task sqid is malformed;
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad filter values.
    /// </returns>
    System.Threading.Tasks.Task<Result<WorkflowTaskHistoryPageDto>> GetHistoryAsync(
        string workflowTaskSqid,
        WorkflowTaskHistoryFilterDto filter,
        System.Threading.CancellationToken cancellationToken = default);
}
