using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Decisions;

/// <summary>
/// Generic decision engine — the heart of the configurable Social-Protection runtime.
/// Each of the 81+ life-event services declares its eligibility + amount logic as JSON
/// on <c>ServicePassport.DecisionRulesJson</c>; this engine interprets that JSON.
/// <para>
/// Pure synchronous: no I/O, no clock access (consumes UTC dates from facts), no exceptions
/// for business outcomes. Implementations MUST be thread-safe so they can be registered
/// as singletons.
/// </para>
/// </summary>
public interface IDecisionEngine
{
    /// <summary>
    /// Evaluates the given <paramref name="ruleSetJson"/> against <paramref name="facts"/>.
    /// </summary>
    /// <param name="ruleSetJson">
    /// The declarative rule-set (see <see cref="JsonRulesDecisionEngine"/> for the DSL shape).
    /// </param>
    /// <param name="facts">The strongly-typed fact bag supplied by the caller.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the <see cref="DecisionOutcome"/> on
    /// successful evaluation (whether eligible or not — eligibility is on the outcome).
    /// <see cref="Result{T}.Failure(string, string)"/> with one of:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.BadRule"/> — malformed JSON / unknown rule kind.</item>
    ///   <item><see cref="ErrorCodes.MissingFact"/> — a rule referenced an absent fact.</item>
    ///   <item><see cref="ErrorCodes.AmountComputationFailed"/> — eligibility passed but
    ///         the amount block could not be evaluated.</item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
    Result<DecisionOutcome> Evaluate(string ruleSetJson, DecisionFacts facts);
}
