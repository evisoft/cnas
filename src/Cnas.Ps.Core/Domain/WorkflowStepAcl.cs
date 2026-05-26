using System.Collections.Generic;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0126 / CF 16.10 — per-step ACL refinement layered on top of
/// <see cref="WorkflowDefinition.AllowedRoles"/> + <see cref="WorkflowDefinition.AllowedGroups"/>.
/// Each row narrows access for ONE step of ONE workflow: in addition to the workflow-level
/// gate, the caller must intersect the step's <see cref="RequiredRoles"/> /
/// <see cref="RequiredGroups"/> AND carry <see cref="RequiredPermission"/> when non-null.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on (<see cref="WorkflowDefinitionId"/>,
/// <see cref="StepCode"/>). At most one ACL row per (workflow, step); the CRUD service's
/// upsert applies this invariant idempotently and the DB index is the safety net.
/// </para>
/// <para>
/// <b>Step codes.</b> <see cref="StepCode"/> matches the canonical step identifier as
/// it appears inside <see cref="WorkflowDefinition.DefinitionJson"/> (BPMN
/// <c>activity-id</c>) and as carried on <c>WorkflowTask.Title</c>-side metadata. The
/// service does NOT cross-check against the workflow JSON — operators can pre-create an
/// ACL for a step that will exist in a future workflow version. The ACL resolver only
/// kicks in when an actual task with that step code surfaces.
/// </para>
/// <para>
/// <b>Conjunctive composition.</b> Per R0126 the per-step gate is AND-ed with the
/// workflow-level gate: a workflow-level allow does not bypass step-level requirements,
/// and a step-level allow does not bypass the workflow-level gate. The only escape
/// hatch is the global <c>cnas-tech-admin</c> super-admin role checked first by
/// <c>IWorkflowAclService.CanHandleAsync</c>.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the CRUD
/// service exposes step ACLs over an admin REST surface; the surrogate id round-trips
/// as a Sqid string per CLAUDE.md RULE 3. The natural key (workflow Sqid + step code)
/// is the canonical route handle.
/// </para>
/// </remarks>
public sealed class WorkflowStepAcl : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the <see cref="WorkflowDefinition"/> whose step this ACL row governs. The
    /// ACL is bound to the workflow's SURROGATE id, so a new revision (created via
    /// R0129 versioning) gets a fresh id and inherits NO ACL rows until an operator
    /// re-provisions them. This intentional non-inheritance keeps version transitions
    /// explicit — operators must affirm the ACL set on every republish.
    /// </summary>
    public long WorkflowDefinitionId { get; set; }

    /// <summary>
    /// Stable step identifier matching the BPMN activity id (or the canonical step code
    /// used on <c>WorkflowTask</c> metadata). Capped at 64 characters at the EF layer.
    /// Operators choose the code; the ACL resolver matches case-sensitively.
    /// </summary>
    public required string StepCode { get; set; }

    /// <summary>
    /// Role codes required IN ADDITION to the workflow-level
    /// <see cref="WorkflowDefinition.AllowedRoles"/>. Empty list means "no extra role
    /// requirement at this step" — the workflow-level gate alone applies. The
    /// intersection is non-empty when at least one of the caller's roles appears in
    /// this list; the workflow-level intersection must succeed independently.
    /// </summary>
    public List<string> RequiredRoles { get; set; } = new();

    /// <summary>
    /// Group codes required IN ADDITION to the workflow-level
    /// <see cref="WorkflowDefinition.AllowedGroups"/>. Same intersection semantics as
    /// <see cref="RequiredRoles"/>. Group membership lives on
    /// <see cref="UserProfile.Groups"/>.
    /// </summary>
    public List<string> RequiredGroups { get; set; } = new();

    /// <summary>
    /// Optional single explicit permission code that the caller must carry to act on
    /// this step (e.g. <c>WorkflowTask.HandleDecisionStep</c>). When non-null the ACL
    /// check additionally requires the caller's role set to include the permission
    /// code verbatim — the codebase's RBAC model treats permissions as named roles for
    /// this check. <c>null</c> means "no explicit permission requirement".
    /// </summary>
    /// <remarks>
    /// The validator enforces the regex <c>^[A-Z][A-Za-z0-9.]+$</c> when non-null so a
    /// stray empty / lowercase / whitespace value cannot silently pass the gate.
    /// </remarks>
    public string? RequiredPermission { get; set; }

    /// <summary>
    /// Free-form admin-facing description of why this step's ACL exists. Surfaces in the
    /// admin UI and the audit trail of mutations. Capped at 512 characters at the EF
    /// layer. <c>null</c> when not set.
    /// </summary>
    public string? Description { get; set; }
}
