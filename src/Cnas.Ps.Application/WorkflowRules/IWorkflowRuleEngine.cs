using System.Collections.Generic;

namespace Cnas.Ps.Application.WorkflowRules;

/// <summary>
/// R0124 / CF 16.08 — outcome of a workflow-lifecycle business-rule evaluation.
/// Returned by every <see cref="IWorkflowRuleEngine"/> method. The contract is
/// uniform across the three lifecycle stages (start, transition, completion) so
/// callers can share an evaluation-result handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allowed semantics.</b> <see cref="Allowed"/> = <c>true</c> means the stage may
/// proceed; <see cref="Allowed"/> = <c>false</c> means the engine refused. A refusal
/// MUST carry a non-null <see cref="BlockReason"/> so the caller can return a
/// human-readable error.
/// </para>
/// <para>
/// <b>Annotations.</b> Optional derived fields the engine wants attached to the
/// resulting decision document / audit row. Keys are stable strings (e.g.
/// <c>"recomputedCategory"</c>); values are short string projections of whatever the
/// rule pack computed. Callers are free to merge or ignore.
/// </para>
/// <para>
/// <b>Pass-through when no pack configured.</b> A workflow without a rule-pack code at
/// the relevant stage produces <c>(Allowed=true, BlockReason=null, Annotations=null)</c>.
/// The engine never returns failure for the "nothing configured" case so the caller's
/// integration cost is zero on workflows that have not opted in.
/// </para>
/// </remarks>
/// <param name="Allowed">
/// <c>true</c> when the rule pack permits the stage to proceed; <c>false</c> when the
/// pack actively refused (the caller must return the
/// <see cref="Cnas.Ps.Application.WorkflowAcl.WorkflowAclConstants.WorkflowRuleBlockedCode"/>
/// error code).
/// </param>
/// <param name="BlockReason">
/// Non-null when <see cref="Allowed"/> is <c>false</c>; carries the stable
/// SCREAMING_SNAKE_CASE reason emitted by the underlying rule pack OR
/// <see cref="Cnas.Ps.Application.WorkflowAcl.WorkflowAclConstants.RuleEngineErrorReason"/>
/// when the engine itself errored. <c>null</c> when <see cref="Allowed"/> is <c>true</c>.
/// </param>
/// <param name="Annotations">
/// Optional derived-field map. <c>null</c> when the engine produced no annotations.
/// </param>
public sealed record RuleEvaluationResult(
    bool Allowed,
    string? BlockReason,
    IReadOnlyDictionary<string, string>? Annotations)
{
    /// <summary>Convenience factory for the no-op "no rules configured" success.</summary>
    /// <returns>An allowed result with no block reason or annotations.</returns>
    public static RuleEvaluationResult Allow() => new(Allowed: true, BlockReason: null, Annotations: null);

    /// <summary>Convenience factory for the "rule pack actively refused" failure.</summary>
    /// <param name="reason">Stable SCREAMING_SNAKE_CASE block reason.</param>
    /// <returns>A blocked result carrying the supplied reason.</returns>
    public static RuleEvaluationResult Block(string reason) => new(Allowed: false, BlockReason: reason, Annotations: null);
}

/// <summary>
/// R0124 / CF 16.08 — workflow-lifecycle rule-evaluation seam. Implementations resolve
/// the rule-pack code from the <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition"/>
/// (<c>StartRulePackCode</c> / <c>TransitionRulePackCode</c> /
/// <c>CompletionRulePackCode</c>) and delegate to the registered
/// <see cref="IWorkflowRulePackEvaluator"/>. The three methods correspond to the
/// three lifecycle hooks; pack-less stages return
/// <see cref="RuleEvaluationResult.Allow"/> without touching the evaluator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure containment.</b> An unhandled exception from the evaluator MUST be
/// translated into a blocked result carrying
/// <see cref="Cnas.Ps.Application.WorkflowAcl.WorkflowAclConstants.RuleEngineErrorReason"/>
/// — letting the exception bubble would create a non-deterministic gate. The
/// implementation logs structured diagnostics before returning.
/// </para>
/// <para>
/// <b>Telemetry.</b> Every evaluation increments
/// <c>cnas.workflow.rule.evaluated{stage, allowed}</c> on the shared CNAS meter so
/// operators can chart per-stage block rates.
/// </para>
/// </remarks>
public interface IWorkflowRuleEngine
{
    /// <summary>
    /// Evaluates the workflow's <c>StartRulePackCode</c> against the application
    /// context. Returns <see cref="RuleEvaluationResult.Allow"/> when the workflow has
    /// no start-stage pack configured.
    /// </summary>
    /// <param name="workflowDefinitionId">Surrogate id of the workflow definition.</param>
    /// <param name="serviceApplicationId">Surrogate id of the application starting the case.</param>
    /// <param name="context">Optional fact bag forwarded to the rule pack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluation result (always non-null).</returns>
    Task<RuleEvaluationResult> EvaluateStartAsync(
        long workflowDefinitionId,
        long serviceApplicationId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates the workflow's <c>TransitionRulePackCode</c> for a task transition
    /// (claim → in-progress, in-progress → complete, manual move to next step, etc.).
    /// </summary>
    /// <param name="workflowTaskId">Surrogate id of the task being transitioned.</param>
    /// <param name="fromStep">Step code the task is leaving.</param>
    /// <param name="toStep">Step code the task is entering.</param>
    /// <param name="context">Optional fact bag forwarded to the rule pack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluation result (always non-null).</returns>
    Task<RuleEvaluationResult> EvaluateTransitionAsync(
        long workflowTaskId,
        string fromStep,
        string toStep,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates the workflow's <c>CompletionRulePackCode</c> when the last task of a
    /// case completes. Annotations on the result are merged into the resulting
    /// decision document.
    /// </summary>
    /// <param name="workflowDefinitionId">Surrogate id of the workflow definition.</param>
    /// <param name="serviceApplicationId">Surrogate id of the completing application.</param>
    /// <param name="context">Optional fact bag forwarded to the rule pack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluation result (always non-null).</returns>
    Task<RuleEvaluationResult> EvaluateCompletionAsync(
        long workflowDefinitionId,
        long serviceApplicationId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default);
}

/// <summary>
/// R0124 — lightweight shim over the underlying rule-pack store consulted by
/// <see cref="IWorkflowRuleEngine"/>. A separate interface so the engine wiring can
/// remain stable across rule-pack backing changes (in-memory placeholder today,
/// future decision-engine binding tomorrow).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shim.</b> The existing <c>IDecisionEngine</c> evaluates JSON DSL strings,
/// not "rule pack codes". Until a per-environment rule-pack registry is provisioned,
/// this shim's reference implementation is a placeholder that returns
/// <see cref="RuleEvaluationResult.Allow"/> for known codes and a
/// <see cref="WorkflowRulePackEvaluatorResult.NotFound"/> verdict for unknown ones.
/// The full decision-engine rebinding is documented as deferred in the iteration
/// report.
/// </para>
/// </remarks>
public interface IWorkflowRulePackEvaluator
{
    /// <summary>
    /// Evaluates the supplied rule-pack code against the supplied stage context.
    /// </summary>
    /// <param name="rulePackCode">Stable rule-pack code from the workflow definition.</param>
    /// <param name="stage">Stage label (<c>Start</c> | <c>Transition</c> | <c>Completion</c>).</param>
    /// <param name="context">Optional fact bag forwarded by the engine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The shim's verdict — either Allow / Block / NotFound — translated by the engine.</returns>
    Task<WorkflowRulePackEvaluatorResult> EvaluateAsync(
        string rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default);
}

/// <summary>
/// R0124 — verdict returned by an <see cref="IWorkflowRulePackEvaluator"/> evaluation.
/// </summary>
/// <param name="Outcome">Stable outcome label (Allow / Block / NotFound).</param>
/// <param name="Reason">Block reason when <see cref="Outcome"/> is <c>Block</c>; otherwise null.</param>
/// <param name="Annotations">Optional derived-field map carried back to the caller.</param>
public sealed record WorkflowRulePackEvaluatorResult(
    WorkflowRulePackOutcome Outcome,
    string? Reason,
    IReadOnlyDictionary<string, string>? Annotations)
{
    /// <summary>Convenience factory for the "allowed" verdict with no annotations.</summary>
    public static WorkflowRulePackEvaluatorResult Allow() =>
        new(WorkflowRulePackOutcome.Allow, Reason: null, Annotations: null);

    /// <summary>Convenience factory for the "allowed with annotations" verdict.</summary>
    /// <param name="annotations">Derived-field map.</param>
    public static WorkflowRulePackEvaluatorResult AllowWith(IReadOnlyDictionary<string, string> annotations) =>
        new(WorkflowRulePackOutcome.Allow, Reason: null, Annotations: annotations);

    /// <summary>Convenience factory for the "block" verdict with the supplied reason.</summary>
    /// <param name="reason">SCREAMING_SNAKE_CASE block reason.</param>
    public static WorkflowRulePackEvaluatorResult Block(string reason) =>
        new(WorkflowRulePackOutcome.Block, Reason: reason, Annotations: null);

    /// <summary>Convenience factory for the "rule pack code not found" verdict.</summary>
    public static WorkflowRulePackEvaluatorResult NotFound() =>
        new(WorkflowRulePackOutcome.NotFound, Reason: null, Annotations: null);
}

/// <summary>R0124 — outcome enum carried by <see cref="WorkflowRulePackEvaluatorResult"/>.</summary>
public enum WorkflowRulePackOutcome
{
    /// <summary>The rule pack permits the stage to proceed.</summary>
    Allow,

    /// <summary>The rule pack actively refuses; <see cref="WorkflowRulePackEvaluatorResult.Reason"/> is populated.</summary>
    Block,

    /// <summary>The rule pack code is unknown to the evaluator — the engine treats this as a block.</summary>
    NotFound,
}

/// <summary>R0124 — stable stage labels emitted on the rule-evaluation counter and forwarded to the shim.</summary>
public static class WorkflowRuleStages
{
    /// <summary>Workflow case start — application submission.</summary>
    public const string Start = "Start";

    /// <summary>Task transition — claim / progress / complete.</summary>
    public const string Transition = "Transition";

    /// <summary>Workflow case completion — last task closed.</summary>
    public const string Completion = "Completion";
}
