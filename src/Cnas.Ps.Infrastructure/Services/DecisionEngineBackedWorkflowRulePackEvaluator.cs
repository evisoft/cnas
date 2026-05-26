using System.Collections.Generic;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0124 (continuation) — production
/// <see cref="IWorkflowRulePackEvaluator"/> binding that bridges the workflow
/// rule-engine seam onto the <see cref="IRulePackBackend"/> facade. Replaces the
/// always-allow <see cref="InMemoryWorkflowRulePackEvaluator"/> placeholder in DI;
/// the backend underneath remains pluggable (today
/// <see cref="NoopRulePackBackend"/>, tomorrow a live DMN / JSON rule-pack runtime
/// gated on R1502 / R0942).
/// </summary>
/// <remarks>
/// <para>
/// <b>Translation contract.</b>
/// </para>
/// <list type="bullet">
///   <item><description>Backend returns <see cref="RulePackBackendOutcome.Allow"/> →
///   evaluator returns <see cref="WorkflowRulePackEvaluatorResult.Allow"/> (annotations
///   propagated when present).</description></item>
///   <item><description>Backend returns <see cref="RulePackBackendOutcome.Block"/>
///   with a reason → evaluator returns
///   <see cref="WorkflowRulePackEvaluatorResult.Block(string)"/> carrying the same
///   reason (the engine layer then surfaces it as the user-facing block reason).</description></item>
///   <item><description>Backend throws unexpectedly → evaluator returns
///   <see cref="WorkflowRulePackEvaluatorResult.Block(string)"/> with
///   <see cref="WorkflowAclConstants.RuleEngineErrorReason"/> and logs the exception
///   at <c>LogError</c>. Cancellation passes through unmodified.</description></item>
/// </list>
/// <para>
/// <b>Telemetry.</b> Every backend round-trip increments
/// <see cref="CnasMeter.WorkflowRuleDecisionEngineInvoked"/> tagged with
/// <c>outcome</c> = <c>allow</c> | <c>deny</c> | <c>error</c>. The companion
/// engine-level <see cref="CnasMeter.WorkflowRuleEvaluated"/> counter continues to
/// fire one layer up; the two counters together let operators chart pack-skipped vs.
/// pack-invoked rates side-by-side.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Implementation is stateless (delegates straight to the
/// backend); the type is registered as a singleton so the per-request engine seam
/// can share one bridge.
/// </para>
/// </remarks>
public sealed class DecisionEngineBackedWorkflowRulePackEvaluator : IWorkflowRulePackEvaluator
{
    private const string OutcomeAllow = "allow";
    private const string OutcomeDeny = "deny";
    private const string OutcomeError = "error";

    private readonly IRulePackBackend _backend;
    private readonly ILogger<DecisionEngineBackedWorkflowRulePackEvaluator> _logger;

    /// <summary>Constructs the evaluator bridge with its DI dependencies.</summary>
    /// <param name="backend">Underlying rule-pack execution backend.</param>
    /// <param name="logger">Structured logger for failure diagnostics.</param>
    public DecisionEngineBackedWorkflowRulePackEvaluator(
        IRulePackBackend backend,
        ILogger<DecisionEngineBackedWorkflowRulePackEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(logger);
        _backend = backend;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WorkflowRulePackEvaluatorResult> EvaluateAsync(
        string rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulePackCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        RulePackBackendResult backendResult;
        try
        {
            backendResult = await _backend
                .EvaluateAsync(rulePackCode, stage, context, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cooperative cancellation must propagate untouched — the engine layer
            // also re-throws it. We do NOT bump the counter on cancel because the
            // backend call is conceptually "not completed".
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Rule-pack backend invocation failed for pack {RulePackCode} stage {Stage}.",
                rulePackCode, stage);
            IncrementCounter(OutcomeError);
            return WorkflowRulePackEvaluatorResult.Block(WorkflowAclConstants.RuleEngineErrorReason);
        }

        switch (backendResult.Outcome)
        {
            case RulePackBackendOutcome.Allow:
                IncrementCounter(OutcomeAllow);
                return backendResult.Annotations is { Count: > 0 }
                    ? WorkflowRulePackEvaluatorResult.AllowWith(backendResult.Annotations)
                    : WorkflowRulePackEvaluatorResult.Allow();

            case RulePackBackendOutcome.Block:
                IncrementCounter(OutcomeDeny);
                return WorkflowRulePackEvaluatorResult.Block(backendResult.Reason ?? "RULE_BLOCKED");

            default:
                // Defensive — a future enum value would otherwise fall through.
                IncrementCounter(OutcomeError);
                return WorkflowRulePackEvaluatorResult.Block(WorkflowAclConstants.RuleEngineErrorReason);
        }
    }

    /// <summary>
    /// Emits one measurement on the <c>cnas.workflow.rule.decision_engine_invoked</c>
    /// counter tagged with the supplied outcome label. Centralised so the three
    /// outcome paths share identical tagging.
    /// </summary>
    /// <param name="outcome">Stable outcome label (<c>allow</c>, <c>deny</c>, <c>error</c>).</param>
    private static void IncrementCounter(string outcome)
    {
        CnasMeter.WorkflowRuleDecisionEngineInvoked.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome));
    }
}

/// <summary>
/// R0124 (continuation) — placeholder <see cref="IRulePackBackend"/> implementation
/// that ALWAYS returns <see cref="RulePackBackendResult.Allow"/>. Used in production
/// DI until the live rule-pack runtime (R1502 / R0942) lands. Every invocation logs
/// a single <c>Information</c>-level structured entry so operators can confirm the
/// no-op path is what is being exercised and not a misrouted production backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ship a no-op now.</b> Wiring the
/// <see cref="DecisionEngineBackedWorkflowRulePackEvaluator"/> into DI without a
/// concrete backend would break composition; the no-op preserves R0124's contract
/// (workflows that opt into rule packs do not regress) while leaving a clear
/// observability seam (the <c>allow</c> tag on the new counter + the Information log)
/// that operators can chart to confirm the placeholder is what is running.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Stateless; safe for singleton registration.
/// </para>
/// </remarks>
public sealed class NoopRulePackBackend : IRulePackBackend
{
    private readonly ILogger<NoopRulePackBackend> _logger;

    /// <summary>Constructs the backend with its DI dependencies.</summary>
    /// <param name="logger">Structured logger used to record each no-op invocation.</param>
    public NoopRulePackBackend(ILogger<NoopRulePackBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<RulePackBackendResult> EvaluateAsync(
        string rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulePackCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        _logger.LogInformation(
            "NoopRulePackBackend allowing rule pack {RulePackCode} for stage {Stage} (no live rule runtime configured).",
            rulePackCode, stage);
        return Task.FromResult(RulePackBackendResult.Allow());
    }
}
