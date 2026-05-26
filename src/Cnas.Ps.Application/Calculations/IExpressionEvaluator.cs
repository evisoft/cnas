using System.Collections.Generic;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Calculations;

/// <summary>
/// R0143 / CF 17.19 — minimal arithmetic-expression evaluator used by the
/// per-passport calc-formula matrix. The evaluator supports a deliberately tiny
/// language so it remains auditable end-to-end and free of dynamic-code or SQL-eval
/// surface:
/// <list type="bullet">
///   <item>Operators <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> (left-to-right with usual precedence).</item>
///   <item>Decimal literals (invariant culture, period as decimal separator).</item>
///   <item>Named inputs supplied at evaluation time (identifiers starting with a letter or <c>_</c>,
///     continuing with letters, digits, or <c>_</c>).</item>
///   <item>Parentheses <c>(</c> and <c>)</c> for grouping.</item>
///   <item>Unary minus (e.g. <c>-x</c> or <c>3 * -2</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hand-rolled.</b> CLAUDE.md cardinal rules forbid Roslyn / NCalc / SQL-eval
/// surface for user-supplied expressions. The Shunting-yard implementation is small,
/// pure, and exercisable from unit tests without a database or runtime sandbox.
/// </para>
/// <para>
/// <b>Error contract.</b> The evaluator never throws on bad input. Every failure
/// surfaces through a <see cref="Result{T}"/> failure carrying one of three stable
/// codes: <see cref="ErrorCodes.ExpressionInvalid"/>,
/// <see cref="ErrorCodes.ExpressionUnknownInput"/>, or
/// <see cref="ErrorCodes.ExpressionDivideByZero"/>.
/// </para>
/// </remarks>
public interface IExpressionEvaluator
{
    /// <summary>
    /// Evaluates the supplied <paramref name="expression"/> against the
    /// <paramref name="inputs"/> dictionary and returns the resulting decimal value.
    /// </summary>
    /// <param name="expression">The expression in the supported sub-language.</param>
    /// <param name="inputs">
    /// Named-input bindings (case-sensitive). Identifiers referenced by
    /// <paramref name="expression"/> but absent from this dictionary surface as
    /// <see cref="ErrorCodes.ExpressionUnknownInput"/> failures.
    /// </param>
    /// <returns>
    /// Success carrying the computed value, or failure carrying one of the stable
    /// expression-error codes described on <see cref="IExpressionEvaluator"/>.
    /// </returns>
    Result<decimal> Evaluate(string expression, IReadOnlyDictionary<string, decimal> inputs);
}
