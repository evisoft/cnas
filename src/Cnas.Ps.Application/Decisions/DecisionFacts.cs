using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Decisions;

/// <summary>
/// Strongly-typed bag of facts handed to <see cref="IDecisionEngine"/> when evaluating
/// a service passport rule-set. The engine itself works with raw types — Sqid encoding
/// happens only at the DTO boundary (CLAUDE.md RULE 3).
/// <para>
/// Supported value runtime types: <see cref="string"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/> (UTC),
/// <see cref="Cnas.Ps.Core.ValueObjects.Money"/>, <see cref="Cnas.Ps.Core.ValueObjects.Idnp"/>,
/// <see cref="Cnas.Ps.Core.ValueObjects.Idno"/>, <see cref="Cnas.Ps.Core.ValueObjects.PercentRate"/>,
/// <see cref="Cnas.Ps.Core.ValueObjects.DateRangeUtc"/>. Other types are accepted but rules
/// referencing them may produce <see cref="ErrorCodes.BadRule"/> at evaluation time.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var facts = new DecisionFacts(new Dictionary&lt;string, object?&gt;
/// {
///     ["isInsured"] = true,
///     ["birthOrder"] = 2,
/// });
///
/// if (facts.Require&lt;bool&gt;("isInsured") is { IsSuccess: true, Value: true })
/// {
///     // proceed
/// }
/// </code>
/// </example>
public sealed class DecisionFacts
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    /// <summary>
    /// Wraps the supplied dictionary. The dictionary is taken by reference — callers
    /// should treat it as immutable for the lifetime of this instance.
    /// </summary>
    /// <param name="values">The fact bag. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public DecisionFacts(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>The underlying fact dictionary (read-only).</summary>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>
    /// Attempts to read a fact of type <typeparamref name="T"/>. Returns <see langword="false"/>
    /// when the key is missing, the value is <see langword="null"/>, or the value's runtime
    /// type does not match <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected fact runtime type.</typeparam>
    /// <param name="key">The fact key.</param>
    /// <param name="value">The strongly-typed value on success; default(T) on miss.</param>
    /// <returns><see langword="true"/> when the fact is present and typed correctly.</returns>
    /// <example>
    /// <code>
    /// if (facts.TryGet&lt;int&gt;("birthOrder", out var order)) { /* ... */ }
    /// </code>
    /// </example>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads a required fact. Returns <see cref="ErrorCodes.MissingFact"/> when absent
    /// or null, or <see cref="ErrorCodes.BadRule"/> when present but of the wrong type.
    /// </summary>
    /// <typeparam name="T">The expected fact runtime type.</typeparam>
    /// <param name="key">The fact key.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the value, or a failure result with
    /// the appropriate error code.
    /// </returns>
    /// <example>
    /// <code>
    /// var insured = facts.Require&lt;bool&gt;("isInsured");
    /// if (insured.IsFailure) return Result&lt;X&gt;.From((Result)insured); // propagate
    /// </code>
    /// </example>
    public Result<T> Require<T>(string key)
    {
        if (!_values.TryGetValue(key, out var raw) || raw is null)
        {
            return Result<T>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{key}' was not supplied.");
        }

        if (raw is T typed)
        {
            return Result<T>.Success(typed);
        }

        return Result<T>.Failure(
            ErrorCodes.BadRule,
            $"Fact '{key}' has runtime type '{raw.GetType().Name}', expected '{typeof(T).Name}'.");
    }
}
