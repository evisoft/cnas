namespace Cnas.Ps.Contracts;

/// <summary>
/// R0126 / CF 16.10 — per-step ACL projection returned by the admin step-ACL
/// endpoints. Carries the Sqid-encoded row id, the Sqid-encoded parent workflow id,
/// the canonical step code, and the role/group/permission requirements.
/// </summary>
/// <remarks>
/// Mass-assignment protection (CLAUDE.md §2.4) — input flows through
/// <see cref="WorkflowStepAclUpsertInput"/>, which deliberately omits surrogate ids,
/// audit fields, and the soft-delete flag. The output exposes the id for round-trip
/// linking only.
/// </remarks>
/// <param name="Id">Sqid-encoded surrogate id of the ACL row.</param>
/// <param name="WorkflowDefinitionId">Sqid-encoded id of the workflow definition this ACL governs.</param>
/// <param name="StepCode">Canonical step code (BPMN activity-id form).</param>
/// <param name="RequiredRoles">
/// Role codes required IN ADDITION to the workflow-level
/// <c>WorkflowDefinition.AllowedRoles</c>. Empty list means no extra role requirement
/// at this step.
/// </param>
/// <param name="RequiredGroups">
/// Group codes required IN ADDITION to the workflow-level
/// <c>WorkflowDefinition.AllowedGroups</c>.
/// </param>
/// <param name="RequiredPermission">
/// Optional single permission code (e.g. <c>WorkflowTask.HandleDecisionStep</c>);
/// null means no explicit permission requirement.
/// </param>
/// <param name="Description">Free-form admin-facing description; nullable.</param>
public sealed record WorkflowStepAclDto(
    string Id,
    string WorkflowDefinitionId,
    string StepCode,
    IReadOnlyList<string> RequiredRoles,
    IReadOnlyList<string> RequiredGroups,
    string? RequiredPermission,
    string? Description);

/// <summary>
/// R0126 / CF 16.10 — request body for the upsert endpoint
/// <c>PUT /api/workflow-definitions/{workflowSqid}/step-acls/{stepCode}</c>. The
/// workflow id + step code live in the route; only the requirement fields appear in
/// the body. Mass-assignment protection (CLAUDE.md §2.4) is enforced by the absence
/// of any id / audit / system fields on this input.
/// </summary>
/// <param name="RequiredRoles">
/// Role codes required IN ADDITION to the workflow-level ACL. May be empty.
/// </param>
/// <param name="RequiredGroups">
/// Group codes required IN ADDITION to the workflow-level ACL. May be empty.
/// </param>
/// <param name="RequiredPermission">
/// Optional single permission code matching the regex
/// <c>^[A-Z][A-Za-z0-9.]+$</c> when non-null; null means no explicit permission
/// requirement.
/// </param>
/// <param name="Description">Optional admin-facing description.</param>
public sealed record WorkflowStepAclUpsertInput(
    IReadOnlyList<string> RequiredRoles,
    IReadOnlyList<string> RequiredGroups,
    string? RequiredPermission,
    string? Description);
