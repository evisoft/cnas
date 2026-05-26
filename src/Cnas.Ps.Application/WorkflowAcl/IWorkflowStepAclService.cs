using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.WorkflowAcl;

/// <summary>
/// R0126 / CF 16.10 — admin-facing CRUD surface over the per-workflow per-step ACL
/// registry. Every mutating method writes a Critical
/// <c>WORKFLOW.STEP_ACL.{CREATED|UPDATED|DELETED}</c> audit row capturing the workflow
/// + step code so investigators can replay operator activity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the workflow-definition management
/// policy; the service-layer guard against the "service called without an authenticated
/// principal" case lives here.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful mutation triggers an
/// <see cref="IWorkflowAclService.InvalidateAsync"/> call so the next ACL check picks
/// up the change without waiting for the 60 s background refresh.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Workflow ids round-trip as Sqid strings per CLAUDE.md RULE 3;
/// step codes round-trip as raw natural strings (the BPMN activity id).
/// </para>
/// </remarks>
public interface IWorkflowStepAclService
{
    /// <summary>
    /// Lists every active step ACL bound to the supplied workflow definition. Ordered
    /// by step code ascending. Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the list of step-ACL DTOs.</returns>
    Task<Result<IReadOnlyList<WorkflowStepAclDto>>> ListAsync(string workflowSqid, CancellationToken ct = default);

    /// <summary>
    /// Idempotent upsert: inserts when no row exists for (workflow, step), updates the
    /// existing row otherwise. Returns the resulting projection with the Sqid id.
    /// Triggers a Critical <c>WORKFLOW.STEP_ACL.CREATED</c> or
    /// <c>WORKFLOW.STEP_ACL.UPDATED</c> audit row depending on the insert / update path.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="stepCode">Canonical step code (BPMN activity-id form).</param>
    /// <param name="input">Upsert payload (body).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting step-ACL DTO with the Sqid id assigned.</returns>
    Task<Result<WorkflowStepAclDto>> UpsertAsync(
        string workflowSqid,
        string stepCode,
        WorkflowStepAclUpsertInput input,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the step ACL (flips
    /// <see cref="Cnas.Ps.Core.Domain.AuditableEntity.IsActive"/> to false). The row
    /// remains queryable for audit forensics. Triggers a Critical
    /// <c>WORKFLOW.STEP_ACL.DELETED</c> audit row.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded workflow definition id.</param>
    /// <param name="stepCode">Canonical step code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; <see cref="ErrorCodes.NotFound"/> when no row exists.</returns>
    Task<Result> DeleteAsync(string workflowSqid, string stepCode, CancellationToken ct = default);
}
