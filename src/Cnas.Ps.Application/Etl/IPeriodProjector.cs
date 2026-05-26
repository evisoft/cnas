namespace Cnas.Ps.Application.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — abstract contract over a period-aware ETL projector.
/// Implementations transform one or more source-supersession tables for a given
/// entity into a flattened list of <see cref="PeriodSlice{TSource}"/> rows where
/// each slice carries a consistent value for every projected field.
/// </summary>
/// <typeparam name="TSource">
/// CLR type of the source entity being projected (typically the parent
/// aggregate, e.g. <c>InsuredPerson</c> for the contributor projection). The
/// type parameter is informational — the projection itself returns generic
/// <see cref="PeriodSlice{TSource}"/> records keyed by the supplied entity id.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Stateless.</b> Implementations are scoped (per-request) and read their
/// inputs through the per-request <c>IReadOnlyCnasDbContext</c>; they do NOT
/// write back to the projection store. Wiring the slices into a snapshot table
/// is the orchestrator's job (e.g. <c>IContributorPeriodProjectionService</c>).
/// </para>
/// <para>
/// <b>Stable name.</b> The <see cref="ProjectionName"/> string is the
/// projector's identity in operator logs and audit details. It is part of the
/// public contract — renaming is a breaking change.
/// </para>
/// </remarks>
public interface IPeriodProjector<TSource>
{
    /// <summary>
    /// Stable identifier for the projection (e.g. <c>"Contributor"</c>,
    /// <c>"Payer"</c>). Logged alongside every run and embedded in the audit
    /// details payload.
    /// </summary>
    string ProjectionName { get; }

    /// <summary>
    /// Loads every relevant source-supersession row for
    /// <paramref name="sourceEntityId"/> and returns the flattened slice list.
    /// Implementations rely on the shared <c>PeriodSliceBuilder</c> helper to
    /// keep the boundary-merging algorithm consistent across projections.
    /// </summary>
    /// <param name="sourceEntityId">
    /// Internal raw <c>long</c> id of the source aggregate. Sqid encoding /
    /// decoding happens at the API boundary; the projector works in raw ids.
    /// </param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// Flattened, chronologically-sorted list of <see cref="PeriodSlice{TSource}"/>.
    /// Empty when the source has no rows to project. Slices NEVER overlap and
    /// the union of their <c>[PeriodStartUtc, PeriodEndUtc)</c> intervals
    /// exactly covers the union of the source rows' validity intervals.
    /// </returns>
    Task<IReadOnlyList<PeriodSlice<TSource>>> ProjectAsync(
        long sourceEntityId,
        CancellationToken ct);
}

/// <summary>
/// R0153 / TOR CF 19.05 — single output slice produced by an
/// <see cref="IPeriodProjector{TSource}"/>. The slice describes a half-open
/// time interval <c>[<see cref="PeriodStartUtc"/>,
/// <see cref="PeriodEndUtc"/>)</c> during which every key in
/// <see cref="ResolvedFields"/> mapped to a single, stable value.
/// </summary>
/// <typeparam name="TSource">
/// Source aggregate type — see the <see cref="IPeriodProjector{TSource}"/>
/// docstring. Carried for documentation only; the record itself does not
/// reference the type parameter at runtime.
/// </typeparam>
/// <param name="PeriodStartUtc">
/// Inclusive UTC start of the slice. Always populated.
/// </param>
/// <param name="PeriodEndUtc">
/// Exclusive UTC end of the slice. <see cref="DateTime.MaxValue"/> when the
/// underlying source row is still open (<c>ValidToUtc = null</c>).
/// </param>
/// <param name="ResolvedFields">
/// Map of field name -> resolved value during the slice. Values may be
/// <c>null</c> when no source row covered the slice midpoint for the field.
/// The implementation guarantees that every projector emits the same set of
/// keys for every slice on a given run so downstream consumers can rely on
/// the schema.
/// </param>
public sealed record PeriodSlice<TSource>(
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    IReadOnlyDictionary<string, object?> ResolvedFields);
