using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.WorkflowAcl;

/// <summary>
/// R0126 / CF 16.10 — workflow-scoped ACL gate consulted at every workflow-task
/// mutation entry point (claim, complete, transition). Returns <c>true</c> when the
/// caller is permitted to act on the given step of the given workflow, <c>false</c>
/// otherwise. The service composes the workflow-level
/// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.AllowedRoles"/> /
/// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.AllowedGroups"/> with the
/// step-level <see cref="Cnas.Ps.Core.Domain.WorkflowStepAcl"/> requirements
/// conjunctively.
/// </summary>
/// <remarks>
/// <para>
/// <b>Super-admin escape hatch.</b> A caller carrying the global
/// <c>cnas-tech-admin</c> role bypasses every check unconditionally. This is the
/// emergency-override invariant for break-glass interventions; the audit pipeline
/// still records the action.
/// </para>
/// <para>
/// <b>Hot-path discipline.</b> Implementations are expected to back the lookup with
/// an in-memory cache rebuilt on a short cadence (60 s default) AND on every
/// mutation via <c>InvalidateAsync</c>. The synchronous return + Boolean result keep
/// the integration cost low for the workflow-task service which calls this on
/// every entry point.
/// </para>
/// <para>
/// <b>Fallback for absent ACL.</b> When a workflow has NO
/// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.AllowedRoles"/> /
/// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.AllowedGroups"/> configured
/// AND no <see cref="Cnas.Ps.Core.Domain.WorkflowStepAcl"/> row exists for the step,
/// the service returns <c>true</c> — the controller-level role gates remain the
/// effective check. The ACL adds NOTHING on a workflow that hasn't opted in.
/// </para>
/// </remarks>
public interface IWorkflowAclService
{
    /// <summary>
    /// Returns <c>true</c> when the supplied user is permitted to handle the
    /// (workflow, step) combination, <c>false</c> otherwise.
    /// </summary>
    /// <param name="workflowDefinitionId">Surrogate id of the
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition"/>.</param>
    /// <param name="stepCode">Canonical step code (BPMN activity-id form).</param>
    /// <param name="userId">Internal <see cref="Cnas.Ps.Core.Domain.UserProfile"/> id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> = caller may proceed; <c>false</c> = ACL denial.</returns>
    Task<bool> CanHandleAsync(long workflowDefinitionId, string stepCode, long userId, CancellationToken ct = default);

    /// <summary>
    /// Forces the in-memory ACL cache to rebuild from the latest persisted state.
    /// Called by the CRUD service after every <see cref="Cnas.Ps.Core.Domain.WorkflowStepAcl"/>
    /// mutation so the very next <see cref="CanHandleAsync"/> sees the change without
    /// waiting for the background refresh tick.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task that completes when the snapshot swap is done.</returns>
    Task InvalidateAsync(CancellationToken ct = default);
}

/// <summary>
/// R0126 — stable role code identifying the global super-administrator that bypasses
/// every ACL check. Exposed as a class-level constant so service-layer + tests share
/// the canonical string and a typo cannot diverge the bypass path.
/// </summary>
public static class WorkflowAclConstants
{
    /// <summary>The role code recognised as super-admin / break-glass override.</summary>
    public const string SuperAdminRole = "cnas-tech-admin";

    /// <summary>The stable error code emitted when an ACL check denies a transition.</summary>
    public const string WorkflowAclDeniedCode = "WORKFLOW_ACL_DENIED";

    /// <summary>The stable error code emitted when a rule pack blocks a transition.</summary>
    public const string WorkflowRuleBlockedCode = "WORKFLOW_RULE_BLOCKED";

    /// <summary>The block reason returned when the rule engine fails internally.</summary>
    public const string RuleEngineErrorReason = "RULE_ENGINE_ERROR";

    /// <summary>The block reason returned when a referenced rule pack code is missing.</summary>
    public const string RulePackNotFoundReason = "RULE_PACK_NOT_FOUND";
}
