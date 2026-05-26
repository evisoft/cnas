using System.Collections.Generic;

namespace Cnas.Ps.Application.QueryBudget;

/// <summary>
/// R0167 — read-only envelope wrapping the filter values a caller supplied on a list
/// query. Used by <see cref="IQueryBudgetPolicy"/> rules to decide which fields are
/// MISSING and therefore deserve a refinement hint.
/// </summary>
/// <remarks>
/// <para>
/// The context is intentionally registry-agnostic — every registry's filter DTO is
/// flattened into a single name/value bag at the service-layer call site. This keeps
/// the hint-rule predicates independent of any specific list-input DTO and lets one
/// rule (e.g. "AddDateFilter") be reused across registries.
/// </para>
/// <para>
/// <b>Non-default semantics.</b> <see cref="Has"/> returns <c>true</c> only when the
/// caller supplied a meaningful value. The service-layer call site is responsible for
/// only inserting NON-default values into the bag — a callsite that always inserts
/// every input DTO field would defeat the hint logic.
/// </para>
/// </remarks>
public interface IQueryFilterContext
{
    /// <summary>
    /// The flat name → value bag of caller-supplied filter fields. Keys are case-
    /// sensitive (PascalCase matching the input DTO field names, e.g. <c>"Q"</c>,
    /// <c>"CreatedFromUtc"</c>). Values are arbitrary primitives (<c>string</c>,
    /// <c>DateTime</c>, enums, ...). Empty when the caller supplied no filters at all.
    /// </summary>
    IReadOnlyDictionary<string, object?> ProvidedFilters { get; }

    /// <summary>
    /// Returns <c>true</c> when the caller supplied a non-default value for the named
    /// field, <c>false</c> otherwise (missing key, <c>null</c> value, empty string).
    /// Used by <see cref="RefinementHintRule.AppliesWhen"/> predicates to detect a
    /// filter omission worth nudging the caller about.
    /// </summary>
    /// <param name="fieldName">PascalCase field name as it appears in the input DTO.</param>
    /// <returns><c>true</c> when the field is present and non-default.</returns>
    bool Has(string fieldName);
}

/// <summary>
/// R0167 — default <see cref="IQueryFilterContext"/> backed by a plain dictionary.
/// Service-layer call sites construct one per list query and hand it to the budget
/// evaluation.
/// </summary>
/// <remarks>
/// The implementation rejects empty-string and <see cref="string.IsNullOrWhiteSpace"/>
/// values as "not provided" so a caller passing <c>?q=</c> doesn't falsely satisfy a
/// "free-text required" hint. Other default-checks (e.g. zero <see cref="DateTime"/>)
/// could be added if a future field demands them — keep changes additive.
/// </remarks>
public sealed class QueryFilterContext : IQueryFilterContext
{
    /// <summary>Backing storage; created at construction and exposed read-only.</summary>
    private readonly Dictionary<string, object?> _filters;

    /// <summary>
    /// Constructs an empty context. The caller mutates it via <see cref="With"/>
    /// (immutable builder semantics) so a service-method local can fluently declare
    /// the filters it actually applied without exposing the dictionary directly.
    /// </summary>
    public QueryFilterContext()
        : this(new Dictionary<string, object?>(StringComparer.Ordinal))
    {
    }

    /// <summary>Private copy constructor used by <see cref="With"/>.</summary>
    /// <param name="filters">Seed dictionary; ownership transfers — the caller MUST NOT mutate it after passing.</param>
    private QueryFilterContext(Dictionary<string, object?> filters)
    {
        _filters = filters;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ProvidedFilters => _filters;

    /// <summary>
    /// Returns a fresh context that includes the named filter when
    /// <paramref name="value"/> is non-default, or the current context unchanged when
    /// the value is null / empty / whitespace.
    /// </summary>
    /// <param name="fieldName">PascalCase field name.</param>
    /// <param name="value">Caller-supplied value; null / empty are treated as "not provided".</param>
    /// <returns>An updated context with the field added when it was non-default.</returns>
    public QueryFilterContext With(string fieldName, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (IsDefault(value))
        {
            return this;
        }
        var next = new Dictionary<string, object?>(_filters, StringComparer.Ordinal)
        {
            [fieldName] = value,
        };
        return new QueryFilterContext(next);
    }

    /// <inheritdoc />
    public bool Has(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }
        if (!_filters.TryGetValue(fieldName, out var stored))
        {
            return false;
        }
        return !IsDefault(stored);
    }

    /// <summary>
    /// "Not provided" predicate. Null, empty string, and whitespace-only strings are
    /// treated as absent. Numeric zeros and default <see cref="DateTime"/> are
    /// intentionally NOT treated as absent — a caller who wanted "before 0001-01-01"
    /// is unusual but legitimate; the hint rules should not punish them.
    /// </summary>
    /// <param name="value">Candidate value; nullable.</param>
    /// <returns><c>true</c> when the value should be treated as "missing".</returns>
    private static bool IsDefault(object? value) =>
        value is null ||
        (value is string s && string.IsNullOrWhiteSpace(s));
}
