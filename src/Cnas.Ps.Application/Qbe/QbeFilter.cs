namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — server-side Query-By-Example envelope. Carries an ordered list of
/// field-condition triples (<see cref="Conditions"/>) and a top-level boolean
/// <see cref="Combinator"/> that joins them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Combinator semantics.</b> <c>"AND"</c> (default) intersects the condition set;
/// <c>"OR"</c> unions it. Mixed-precedence trees are intentionally out of scope — the
/// envelope expresses a flat list because the UI form (UI 009) only renders one combinator
/// dropdown at the top of the QBE panel. Callers needing nested expressions can chain
/// multiple list calls together client-side.
/// </para>
/// <para>
/// <b>Caps.</b> The validator enforces a max of 25 conditions and a max of 1024 chars per
/// value. These bounds protect against pathological payloads constructed by a hostile or
/// buggy client.
/// </para>
/// </remarks>
/// <param name="Combinator">
/// Top-level boolean operator — one of <c>"AND"</c> / <c>"OR"</c> (case-sensitive). The
/// converter rejects any other value with
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.QbeInvalidCombinator"/>.
/// </param>
/// <param name="Conditions">
/// Ordered list of conditions to apply. The order is preserved end-to-end so debuggers can
/// trace a problem report back to the offending row in the UI grid. Empty list is allowed
/// (matches everything — equivalent to passing a null filter).
/// </param>
/// <param name="Orderings">
/// R0523 / TOR CF 03.05 — optional multi-field ordering chain. <see langword="null"/> or
/// empty leaves the existing default ordering in place (each service decides what that
/// is — e.g. <c>SolicitantService</c> orders by <c>DisplayName</c> then <c>Id</c>). When
/// supplied, the converter's <c>ApplyOrdering</c> entry-point emits an
/// <c>OrderBy</c>/<c>ThenBy</c> chain that EF Core translates to SQL.
/// </param>
/// <param name="GroupByField">
/// R0523 / TOR CF 03.05 — optional single-field grouping. <see langword="null"/> means
/// "no grouping". Services decide what grouping means for their projection (typically a
/// secondary breakdown returned alongside the page); the field must be a member of the
/// registry's QBE schema. Kept as a single optional field on the envelope because the
/// UI 009 grid surface only renders a single group-by dropdown.
/// </param>
public sealed record QbeFilter(
    string Combinator,
    IReadOnlyList<QbeCondition> Conditions,
    IReadOnlyList<QbeOrdering>? Orderings = null,
    string? GroupByField = null)
{
    /// <summary>The canonical AND combinator literal.</summary>
    public const string CombinatorAnd = "AND";

    /// <summary>The canonical OR combinator literal.</summary>
    public const string CombinatorOr = "OR";

    /// <summary>
    /// Returns an empty filter that matches every row. Convenience constant used by
    /// tests and by callers that want to opt-out of QBE on a specific list call.
    /// </summary>
    public static QbeFilter Empty { get; } = new(CombinatorAnd, Array.Empty<QbeCondition>());

    /// <summary>
    /// Returns <see langword="true"/> when the combinator is the canonical
    /// <see cref="CombinatorOr"/> literal. Case-sensitive — <c>"or"</c> returns
    /// <see langword="false"/> so a typo at the boundary surfaces as a validation error
    /// rather than a silent semantic flip.
    /// </summary>
    public bool IsOr => string.Equals(Combinator, CombinatorOr, StringComparison.Ordinal);
}
