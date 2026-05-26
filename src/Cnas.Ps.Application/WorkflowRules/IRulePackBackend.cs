using System.Collections.Generic;

namespace Cnas.Ps.Application.WorkflowRules;

/// <summary>
/// R0124 — thin facade for the underlying rule-pack execution backend consulted by the
/// production <see cref="IWorkflowRulePackEvaluator"/> binding
/// (<c>DecisionEngineBackedWorkflowRulePackEvaluator</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a facade.</b> The existing
/// <see cref="Cnas.Ps.Application.Decisions.IDecisionEngine"/> evaluates the per-passport
/// <c>DecisionRulesJson</c> against strongly-typed
/// <see cref="Cnas.Ps.Application.Decisions.DecisionFacts"/> — it does NOT speak in terms
/// of "rule-pack codes" + free-form context dictionaries. Translating between the two
/// shapes belongs in a dedicated abstraction so the workflow rule-engine can stay
/// blissfully ignorant of which rule executor lights up underneath: today
/// <c>NoopRulePackBackend</c> (logs + always allows) and tomorrow the live DMN / JSON
/// rule-pack runtime gated on R1502 / R0942.
/// </para>
/// <para>
/// <b>Contract.</b> Implementations MUST be thread-safe (the workflow rule-engine is
/// scoped; the backend is typically a singleton). Implementations MUST NOT throw on
/// business outcomes — return <see cref="RulePackBackendResult.Block"/> instead — and
/// SHOULD only let exceptions escape when the failure is truly exceptional (network
/// timeout, malformed rule pack). The decision-engine-backed evaluator wraps every
/// invocation in a try / catch and emits a structured log + the
/// <c>RULE_ENGINE_ERROR</c> block reason on throw, so escaping exceptions are still
/// contained — but well-behaved backends translate them into proper verdicts.
/// </para>
/// </remarks>
public interface IRulePackBackend
{
    /// <summary>
    /// Evaluates the supplied rule-pack code against the workflow-supplied context bag
    /// and returns a stable verdict.
    /// </summary>
    /// <param name="rulePackCode">
    /// Stable rule-pack code carried by the
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition"/> column at the relevant stage
    /// (<c>StartRulePackCode</c> / <c>TransitionRulePackCode</c> /
    /// <c>CompletionRulePackCode</c>). Implementations MAY look the code up in a
    /// per-environment registry or treat it as a no-op identifier.
    /// </param>
    /// <param name="stage">
    /// Stage label (<c>Start</c> | <c>Transition</c> | <c>Completion</c>) — provided so
    /// backends can branch on lifecycle moment without parsing the pack code.
    /// </param>
    /// <param name="context">
    /// Optional fact bag forwarded by the workflow engine. May be <c>null</c> when the
    /// caller had nothing to add. Implementations MUST tolerate null and MUST NOT
    /// mutate the dictionary.
    /// </param>
    /// <param name="ct">Cancellation token honoured for cooperative cancellation.</param>
    /// <returns>The backend's verdict; the workflow rule-engine translates to a final
    /// <see cref="RuleEvaluationResult"/>.</returns>
    Task<RulePackBackendResult> EvaluateAsync(
        string rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default);
}

/// <summary>
/// R0124 — verdict returned by an <see cref="IRulePackBackend"/> evaluation. Mirrors
/// <see cref="WorkflowRulePackEvaluatorResult"/> but is owned by the backend abstraction
/// so the two contracts can evolve independently (e.g. the backend may grow extra
/// metadata fields without polluting the public evaluator surface).
/// </summary>
/// <param name="Outcome">Stable outcome label (Allow / Block).</param>
/// <param name="Reason">Block reason when <paramref name="Outcome"/> is <c>Block</c>; otherwise null.</param>
/// <param name="Annotations">Optional derived-field map carried back to the caller.</param>
public sealed record RulePackBackendResult(
    RulePackBackendOutcome Outcome,
    string? Reason,
    IReadOnlyDictionary<string, string>? Annotations)
{
    /// <summary>Convenience factory for the "allowed" verdict with no annotations.</summary>
    /// <returns>An Allow verdict with null reason + null annotations.</returns>
    public static RulePackBackendResult Allow() =>
        new(RulePackBackendOutcome.Allow, Reason: null, Annotations: null);

    /// <summary>Convenience factory for the "allowed with annotations" verdict.</summary>
    /// <param name="annotations">Derived-field map.</param>
    /// <returns>An Allow verdict carrying the supplied annotations.</returns>
    public static RulePackBackendResult AllowWith(IReadOnlyDictionary<string, string> annotations) =>
        new(RulePackBackendOutcome.Allow, Reason: null, Annotations: annotations);

    /// <summary>Convenience factory for the "blocked" verdict with the supplied reason.</summary>
    /// <param name="reason">SCREAMING_SNAKE_CASE block reason.</param>
    /// <returns>A Block verdict carrying the supplied reason.</returns>
    public static RulePackBackendResult Block(string reason) =>
        new(RulePackBackendOutcome.Block, Reason: reason, Annotations: null);
}

/// <summary>R0124 — outcome enum carried by <see cref="RulePackBackendResult"/>.</summary>
public enum RulePackBackendOutcome
{
    /// <summary>The backend permits the stage to proceed.</summary>
    Allow,

    /// <summary>The backend actively refuses; <see cref="RulePackBackendResult.Reason"/> is populated.</summary>
    Block,
}
