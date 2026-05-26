using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — produces an access-control verdict for the supplied
/// policy name + attribute context. The evaluator loads the rule set keyed by
/// <c>policyName</c>, parses (or fetches from its in-process cache) each rule's
/// condition expression, iterates rules in
/// <see cref="Cnas.Ps.Core.Domain.AbacRule.OrderIndex"/> ascending order, and
/// returns the first matching rule's
/// <see cref="Cnas.Ps.Core.Domain.AbacEffect"/>. If no rule matches, the
/// substrate returns the rule set's
/// <see cref="Cnas.Ps.Core.Domain.AbacRuleSet.DefaultEffect"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe-by-default failure semantics.</b> Any per-rule parse or runtime
/// failure (including unexpected exceptions) is logged + audited
/// (<c>ABAC.RULE_EVAL_ERROR</c>) and treated as "did not match" — the failing
/// rule is skipped so the next rule (or the default effect) determines the
/// verdict. A malformed rule MUST NEVER silently grant access.
/// </para>
/// <para>
/// <b>Caching.</b> Parsed ASTs are cached in process memory keyed by rule id.
/// Any mutation through the registry calls
/// <see cref="InvalidateCache"/> so subsequent evaluations re-parse from the
/// updated rule text.
/// </para>
/// <para>
/// <b>Lifetime.</b> The evaluator is a Singleton (the cache lives for the
/// process lifetime). Per-evaluation DB reads happen against a Scoped
/// <c>IReadOnlyCnasDbContext</c> resolved from
/// <see cref="System.IServiceProvider"/> on each call — the evaluator does
/// NOT capture the DbContext on its own fields.
/// </para>
/// </remarks>
public interface IAbacRuleEvaluator
{
    /// <summary>
    /// Evaluates the rule set identified by <paramref name="policyName"/>
    /// against <paramref name="context"/> and returns the resulting decision.
    /// </summary>
    /// <param name="policyName">The stable policy name to dispatch against.</param>
    /// <param name="context">The attribute payload to evaluate.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// A populated <see cref="AbacDecisionDto"/> on success. Returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.AbacNotFound"/> when the
    /// policy name is unknown to the substrate.
    /// </returns>
    Task<Result<AbacDecisionDto>> EvaluateAsync(
        string policyName,
        AbacEvaluationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates the parsed-AST cache. Called by
    /// <see cref="IAbacRuleRegistryService"/> after any mutation that may
    /// change how a rule evaluates. Idempotent and safe to call concurrently.
    /// </summary>
    void InvalidateCache();
}
