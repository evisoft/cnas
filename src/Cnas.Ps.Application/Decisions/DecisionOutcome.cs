using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Decisions;

/// <summary>
/// Result of evaluating a service-passport rule-set against a fact bag.
/// <para>
/// On <see cref="IsEligible"/> = <see langword="true"/> the engine populates
/// <see cref="Amount"/> with the computed benefit and writes the passport's
/// <c>successCode</c> into <see cref="ReasonCodes"/>. On <see langword="false"/>
/// the <see cref="Amount"/> is <see langword="null"/> and <see cref="ReasonCodes"/>
/// holds every <c>failCode</c> produced by failing rules (not short-circuited so
/// callers can show every reason in one pass).
/// </para>
/// </summary>
/// <param name="IsEligible">Whether the applicant satisfied every eligibility rule.</param>
/// <param name="Amount">
/// The computed benefit when eligible; <see langword="null"/> when ineligible
/// or when amount computation was skipped.
/// </param>
/// <param name="ReasonCodes">
/// Stable, screaming-snake-case codes (e.g. <c>BIRTH_GRANT_ELIGIBLE</c>,
/// <c>INELIGIBLE_NOT_INSURED</c>) — part of the audit trail; never null, may be empty.
/// </param>
/// <param name="ComputedValues">
/// Intermediate values computed during evaluation (e.g. the looked-up table row).
/// Useful for explainability — the engine never depends on this dictionary itself.
/// </param>
/// <example>
/// <code>
/// var outcome = engine.Evaluate(passport.DecisionRulesJson, facts).Value;
/// if (outcome.IsEligible)
///     Console.WriteLine($"Grant: {outcome.Amount}");
/// else
///     foreach (var code in outcome.ReasonCodes) Console.WriteLine(code);
/// </code>
/// </example>
public sealed record DecisionOutcome(
    bool IsEligible,
    Money? Amount,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyDictionary<string, object?> ComputedValues);
