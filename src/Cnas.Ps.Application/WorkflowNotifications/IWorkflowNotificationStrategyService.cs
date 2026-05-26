using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.WorkflowNotifications;

/// <summary>
/// R0128 / R0173 — admin-facing CRUD surface over the per-workflow notification-strategy
/// registry. Every mutating method writes a Critical
/// <c>WORKFLOW.NOTIFY.STRATEGY.{CREATED|UPDATED|DISABLED}</c> audit row capturing the
/// workflow code + event code so investigators can replay operator activity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the workflow-management policy;
/// here we only guard against the "service called without an authenticated principal"
/// case via <c>ICallerContext.UserId</c> presence.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful mutation triggers an
/// <see cref="IWorkflowNotificationStrategyResolver"/>-side cache invalidation so the
/// next workflow event dispatch picks up the change without waiting for the 60 s
/// background refresh.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every method that emits or consumes a workflow id uses the
/// Sqid string form per CLAUDE.md RULE 3. The natural key (workflow, event code) is
/// addressed via the workflow Sqid + the raw event code in the route, never the
/// surrogate strategy id.
/// </para>
/// </remarks>
public interface IWorkflowNotificationStrategyService
{
    /// <summary>
    /// Lists every active strategy bound to the supplied workflow definition. Ordered
    /// by event code ascending. Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the list of strategy DTOs.</returns>
    Task<Result<IReadOnlyList<WorkflowNotificationStrategyOutput>>> ListAsync(string workflowSqid, CancellationToken ct = default);

    /// <summary>
    /// Fetches the single strategy bound to (workflow, event code), or
    /// <see cref="ErrorCodes.NotFound"/> when no strategy is configured.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition.</param>
    /// <param name="eventCode">Canonical event code from <see cref="WorkflowNotificationEvents"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The strategy DTO on success, <see cref="ErrorCodes.NotFound"/> otherwise.</returns>
    Task<Result<WorkflowNotificationStrategyOutput>> GetByEventAsync(string workflowSqid, string eventCode, CancellationToken ct = default);

    /// <summary>
    /// Idempotent upsert: inserts when no row exists for (workflow, event code), updates
    /// the existing row otherwise. The route's (workflow, event code) is the natural
    /// key; the body carries the configuration fields. Triggers a Critical
    /// <c>WORKFLOW.NOTIFY.STRATEGY.CREATED</c> or <c>...UPDATED</c> audit row depending
    /// on the insert / update path.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition.</param>
    /// <param name="eventCode">Canonical event code from <see cref="WorkflowNotificationEvents"/>.</param>
    /// <param name="input">Upsert payload (body).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting strategy DTO with the Sqid id assigned.</returns>
    Task<Result<WorkflowNotificationStrategyOutput>> UpsertAsync(
        string workflowSqid,
        string eventCode,
        WorkflowNotificationStrategyUpsertInput input,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-disables the strategy: flips <see cref="Core.Domain.AuditableEntity.IsActive"/>
    /// to false so the resolver stops seeing the row. <see cref="Core.Domain.WorkflowNotificationStrategy.IsEnabled"/>
    /// is NOT touched — it remains the explicit "do not notify" override flag for
    /// future re-enable. Writes a Critical <c>WORKFLOW.NOTIFY.STRATEGY.DISABLED</c>
    /// audit row.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition.</param>
    /// <param name="eventCode">Canonical event code from <see cref="WorkflowNotificationEvents"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; <see cref="ErrorCodes.NotFound"/> when no row exists.</returns>
    Task<Result> DisableAsync(string workflowSqid, string eventCode, CancellationToken ct = default);
}
