using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — abstract base of the immutable ABAC expression AST.
/// Each concrete node knows how to evaluate itself against an
/// <see cref="AbacEvaluationContext"/>. The tree is produced by
/// <see cref="IAbacExpressionParser"/> and cached per rule by the evaluator;
/// it is never mutated after construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe-by-default failure semantics.</b> <see cref="Evaluate"/> NEVER
/// throws — every comparison or call that cannot meaningfully resolve
/// (type mismatch, missing identifier, malformed numeric coercion) returns
/// <c>false</c>. This contractually guarantees that a malformed rule cannot
/// silently grant access; the evaluator further wraps any unexpected exception
/// in a try/catch as defense-in-depth.
/// </para>
/// </remarks>
public abstract record AbacExpression
{
    /// <summary>Evaluates this AST node against the supplied context.</summary>
    /// <param name="context">The attribute payload to evaluate against.</param>
    /// <returns><c>true</c> when the node matches; <c>false</c> otherwise.</returns>
    public abstract bool Evaluate(AbacEvaluationContext context);
}

/// <summary>R2271 — disjunction node — evaluates to true when ANY child matches.</summary>
/// <param name="Left">Left operand.</param>
/// <param name="Right">Right operand.</param>
public sealed record AbacOrExpression(AbacExpression Left, AbacExpression Right) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
        => Left.Evaluate(context) || Right.Evaluate(context);
}

/// <summary>R2271 — conjunction node — evaluates to true when ALL children match.</summary>
/// <param name="Left">Left operand.</param>
/// <param name="Right">Right operand.</param>
public sealed record AbacAndExpression(AbacExpression Left, AbacExpression Right) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
        => Left.Evaluate(context) && Right.Evaluate(context);
}

/// <summary>R2271 — logical negation node.</summary>
/// <param name="Inner">Operand whose verdict is flipped.</param>
public sealed record AbacNotExpression(AbacExpression Inner) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
        => !Inner.Evaluate(context);
}

/// <summary>R2271 — operator vocabulary supported by <see cref="AbacComparisonExpression"/>.</summary>
public enum AbacComparisonOperator
{
    /// <summary><c>==</c> — value-equality with numeric coercion for decimals.</summary>
    Equal,

    /// <summary><c>!=</c> — value-inequality.</summary>
    NotEqual,

    /// <summary><c>&lt;</c> — strictly-less numeric comparison.</summary>
    Less,

    /// <summary><c>&lt;=</c> — less-or-equal numeric comparison.</summary>
    LessEqual,

    /// <summary><c>&gt;</c> — strictly-greater numeric comparison.</summary>
    Greater,

    /// <summary><c>&gt;=</c> — greater-or-equal numeric comparison.</summary>
    GreaterEqual,
}

/// <summary>
/// R2271 — binary comparison node. Numeric comparisons coerce both sides to
/// <see cref="decimal"/>; ordering operators that can't coerce evaluate to
/// <c>false</c>. Equality compares values with type-tolerant numeric
/// coercion (<c>1 == 1.0</c>).
/// </summary>
/// <param name="Left">Left value (literal or identifier).</param>
/// <param name="Right">Right value (literal or identifier).</param>
/// <param name="Operator">The comparison operator to apply.</param>
public sealed record AbacComparisonExpression(
    AbacValue Left,
    AbacValue Right,
    AbacComparisonOperator Operator) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
    {
        try
        {
            var leftValue = Left.Resolve(context);
            var rightValue = Right.Resolve(context);
            return Operator switch
            {
                AbacComparisonOperator.Equal => AbacRuntime.ValuesEqual(leftValue, rightValue),
                AbacComparisonOperator.NotEqual => !AbacRuntime.ValuesEqual(leftValue, rightValue),
                AbacComparisonOperator.Less => AbacRuntime.TryCompare(leftValue, rightValue, out var c) && c < 0,
                AbacComparisonOperator.LessEqual => AbacRuntime.TryCompare(leftValue, rightValue, out var c) && c <= 0,
                AbacComparisonOperator.Greater => AbacRuntime.TryCompare(leftValue, rightValue, out var c) && c > 0,
                AbacComparisonOperator.GreaterEqual => AbacRuntime.TryCompare(leftValue, rightValue, out var c) && c >= 0,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>R2271 — membership test — <c>in(value, list…)</c>.</summary>
/// <param name="Target">The value to test for membership.</param>
/// <param name="Candidates">The list of candidate values.</param>
public sealed record AbacInExpression(AbacValue Target, IReadOnlyList<AbacValue> Candidates) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
    {
        try
        {
            var target = Target.Resolve(context);
            foreach (var candidate in Candidates)
            {
                if (AbacRuntime.ValuesEqual(target, candidate.Resolve(context)))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>R2271 — supported string-call kinds for <see cref="AbacStringCallExpression"/>.</summary>
public enum AbacStringCallKind
{
    /// <summary><c>startsWith(value, literal)</c>.</summary>
    StartsWith,

    /// <summary><c>endsWith(value, literal)</c>.</summary>
    EndsWith,

    /// <summary><c>contains(value, literal)</c>.</summary>
    Contains,
}

/// <summary>
/// R2271 — string predicate call. Both operands must resolve to non-null
/// strings; if either is null or non-string the call evaluates to
/// <c>false</c> (safe-by-default).
/// </summary>
/// <param name="Kind">The predicate kind.</param>
/// <param name="Target">Value to test (identifier or literal).</param>
/// <param name="Literal">String literal probe.</param>
public sealed record AbacStringCallExpression(
    AbacStringCallKind Kind,
    AbacValue Target,
    string Literal) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
    {
        try
        {
            var raw = Target.Resolve(context);
            if (raw is not string s)
            {
                return false;
            }
            return Kind switch
            {
                AbacStringCallKind.StartsWith => s.StartsWith(Literal, StringComparison.Ordinal),
                AbacStringCallKind.EndsWith => s.EndsWith(Literal, StringComparison.Ordinal),
                AbacStringCallKind.Contains => s.Contains(Literal, StringComparison.Ordinal),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>R2271 — <c>has(identifier)</c> — true when the identifier resolves to non-null.</summary>
/// <param name="Identifier">Dotted identifier to probe.</param>
public sealed record AbacHasExpression(string Identifier) : AbacExpression
{
    /// <inheritdoc />
    public override bool Evaluate(AbacEvaluationContext context)
    {
        try
        {
            return context.Resolve(Identifier) is not null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// R2271 — discriminated value carrier — either a constant literal OR a dotted
/// identifier resolved at evaluation time. Used inside comparisons and calls.
/// </summary>
public abstract record AbacValue
{
    /// <summary>Resolves this value against the supplied context.</summary>
    /// <param name="context">The attribute payload.</param>
    /// <returns>The runtime value (may be <c>null</c>).</returns>
    public abstract object? Resolve(AbacEvaluationContext context);
}

/// <summary>R2271 — constant string literal value.</summary>
/// <param name="Value">The literal string.</param>
public sealed record AbacStringLiteral(string Value) : AbacValue
{
    /// <inheritdoc />
    public override object? Resolve(AbacEvaluationContext context) => Value;
}

/// <summary>R2271 — constant numeric literal value (carried as <see cref="decimal"/>).</summary>
/// <param name="Value">The literal decimal.</param>
public sealed record AbacNumberLiteral(decimal Value) : AbacValue
{
    /// <inheritdoc />
    public override object? Resolve(AbacEvaluationContext context) => Value;
}

/// <summary>R2271 — constant boolean literal value.</summary>
/// <param name="Value">The literal bool.</param>
public sealed record AbacBoolLiteral(bool Value) : AbacValue
{
    /// <inheritdoc />
    public override object? Resolve(AbacEvaluationContext context) => Value;
}

/// <summary>R2271 — explicit <c>null</c> literal.</summary>
public sealed record AbacNullLiteral : AbacValue
{
    /// <inheritdoc />
    public override object? Resolve(AbacEvaluationContext context) => null;
}

/// <summary>R2271 — dotted identifier reference resolved at evaluation time.</summary>
/// <param name="Identifier">A dotted identifier with one of the four allowed roots.</param>
public sealed record AbacIdentifierValue(string Identifier) : AbacValue
{
    /// <inheritdoc />
    public override object? Resolve(AbacEvaluationContext context) => context.Resolve(Identifier);
}

/// <summary>
/// R2271 — shared runtime helpers used by AST node evaluators. Centralised so
/// the value coercion rules don't drift across node implementations.
/// </summary>
public static class AbacRuntime
{
    /// <summary>
    /// Compares two raw values for equality with type-tolerant numeric
    /// coercion. <c>null</c> equals <c>null</c> only.
    /// </summary>
    /// <param name="left">Left value (possibly null).</param>
    /// <param name="right">Right value (possibly null).</param>
    /// <returns><c>true</c> when the values are considered equal.</returns>
    public static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        // Numeric coercion path — compare as decimal when both sides convert.
        if (TryToDecimal(left, out var ld) && TryToDecimal(right, out var rd))
        {
            return ld == rd;
        }

        // Boolean path — compare directly.
        if (left is bool lb && right is bool rb)
        {
            return lb == rb;
        }

        // String path — ordinal compare.
        if (left is string ls && right is string rs)
        {
            return string.Equals(ls, rs, StringComparison.Ordinal);
        }

        // Fallback to .Equals for opaque types.
        return left.Equals(right);
    }

    /// <summary>
    /// Tries to compare two values numerically as <see cref="decimal"/>. Both
    /// sides must be numerically coercible; otherwise returns <c>false</c>.
    /// </summary>
    /// <param name="left">Left numeric value.</param>
    /// <param name="right">Right numeric value.</param>
    /// <param name="result">Comparison result (-1, 0, +1) when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when both sides converted to decimal and were compared.</returns>
    public static bool TryCompare(object? left, object? right, out int result)
    {
        result = 0;
        if (!TryToDecimal(left, out var ld) || !TryToDecimal(right, out var rd))
        {
            return false;
        }
        result = ld.CompareTo(rd);
        return true;
    }

    /// <summary>
    /// Tries to convert <paramref name="value"/> to a <see cref="decimal"/>.
    /// Supports <see cref="decimal"/>, <see cref="int"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="float"/>, and numeric strings (parsed
    /// with the invariant culture).
    /// </summary>
    /// <param name="value">Candidate value.</param>
    /// <param name="result">The decimal when conversion succeeded.</param>
    /// <returns><c>true</c> when the value converted; <c>false</c> otherwise.</returns>
    public static bool TryToDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case null:
                result = 0m;
                return false;
            case decimal dec:
                result = dec;
                return true;
            case int i32:
                result = i32;
                return true;
            case long i64:
                result = i64;
                return true;
            case double d:
                result = (decimal)d;
                return true;
            case float f:
                result = (decimal)f;
                return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0m;
                return false;
        }
    }
}
