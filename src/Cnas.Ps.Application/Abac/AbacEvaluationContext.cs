using System.Collections.Generic;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — the attribute payload an
/// <see cref="IAbacRuleEvaluator"/> consults when resolving identifiers inside
/// a parsed ABAC condition expression. Four root namespaces (<c>subject</c>,
/// <c>resource</c>, <c>environment</c>, <c>action</c>) are exposed; any other
/// root identifier is rejected by the parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifier resolution.</b> The evaluator splits a dotted identifier such
/// as <c>subject.regionCode</c> on the first dot — the leading segment selects
/// the dictionary (<c>subject</c> → <see cref="Subject"/>, …) and the
/// remainder is the case-sensitive key. Missing keys resolve to <c>null</c>;
/// the comparison/call rules then decide how to interpret <c>null</c> per
/// CLAUDE.md safe-by-default semantics (a malformed comparison returns
/// <c>false</c> rather than throwing).
/// </para>
/// <para>
/// <b>Allowed value types.</b> Each dictionary's values are coerced through
/// the evaluator's runtime conversions — strings, <see cref="decimal"/>,
/// <see cref="bool"/>, and <see cref="long"/> / <see cref="int"/> /
/// <see cref="double"/> are all natively supported; numeric values are
/// compared as <see cref="decimal"/>. Anything else is treated as opaque
/// (equality only).
/// </para>
/// </remarks>
/// <param name="Subject">Attributes describing the calling principal (region code, clearance, roles).</param>
/// <param name="Resource">Attributes describing the target resource (category, owner, region).</param>
/// <param name="Environment">Attributes describing the request context (local hour, IP, channel).</param>
/// <param name="Action">Attributes describing the action being attempted (HTTP method, operation code).</param>
public sealed record AbacEvaluationContext(
    IReadOnlyDictionary<string, object?> Subject,
    IReadOnlyDictionary<string, object?> Resource,
    IReadOnlyDictionary<string, object?> Environment,
    IReadOnlyDictionary<string, object?> Action)
{
    /// <summary>
    /// Resolves a dotted identifier such as <c>subject.regionCode</c> to its
    /// value across the four root dictionaries. Returns <c>null</c> for an
    /// unknown root OR a missing key — the evaluator's safe-by-default rules
    /// then decide how to interpret <c>null</c> in the surrounding comparison.
    /// </summary>
    /// <param name="identifier">A dotted identifier with one of the four allowed roots.</param>
    /// <returns>The resolved value, or <c>null</c> when the identifier does not bind.</returns>
    public object? Resolve(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }
        var dot = identifier.IndexOf('.');
        if (dot <= 0 || dot == identifier.Length - 1)
        {
            return null;
        }
        var root = identifier.AsSpan(0, dot).ToString();
        var key = identifier.AsSpan(dot + 1).ToString();
        var dict = root switch
        {
            "subject" => Subject,
            "resource" => Resource,
            "environment" => Environment,
            "action" => Action,
            _ => null,
        };
        if (dict is null)
        {
            return null;
        }
        return dict.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Builds an empty <see cref="AbacEvaluationContext"/> backed by empty dictionaries.</summary>
    /// <returns>The empty context.</returns>
    public static AbacEvaluationContext Empty() => new(
        Subject: new Dictionary<string, object?>(System.StringComparer.Ordinal),
        Resource: new Dictionary<string, object?>(System.StringComparer.Ordinal),
        Environment: new Dictionary<string, object?>(System.StringComparer.Ordinal),
        Action: new Dictionary<string, object?>(System.StringComparer.Ordinal));
}
