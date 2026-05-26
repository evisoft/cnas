namespace Cnas.Ps.Application.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — pure helper that flattens a set of source
/// supersession rows (each carrying its own <c>(ValidFromUtc, ValidToUtc)</c>
/// window) into a chronologically-ordered list of disjoint slices where each
/// slice carries a single, consistent value per declared field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Collect every distinct <c>ValidFromUtc</c> and <c>ValidToUtc</c>
///       timestamp across the supplied source rows. Open-ended rows
///       (<c>ValidToUtc = null</c>) contribute the configured open-ended
///       sentinel (<see cref="DateTime.MaxValue"/>) instead.
///     </description>
///   </item>
///   <item>
///     <description>
///       Sort and de-duplicate the boundaries chronologically. Consecutive
///       pairs <c>(B[i], B[i+1])</c> form the candidate slice intervals
///       <c>[start, end)</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       For each candidate slice, resolve every field by scanning the source
///       rows for one whose validity interval covers the slice midpoint. When
///       multiple rows qualify, the most-recently-created row wins (ties
///       broken by ascending <see cref="SourceRow.SourceId"/>).
///     </description>
///   </item>
///   <item>
///     <description>
///       Slices where at least one field resolved to a value are emitted; an
///       all-null slice is suppressed to avoid emitting empty padding between
///       distant source rows. The boundary set keeps the slices disjoint by
///       construction.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Open-ended slices.</b> A source row with <c>ValidToUtc = null</c>
/// extends to <see cref="DateTime.MaxValue"/> in the boundary collection, so
/// the resulting outermost slice is
/// <c>[<i>row.ValidFromUtc</i>, <see cref="DateTime.MaxValue"/>)</c>. Reports
/// querying "as-of-now" never trip a NULL-aware code path.
/// </para>
/// </remarks>
public static class PeriodSliceBuilder
{
    /// <summary>
    /// Sentinel <see cref="DateTime"/> used as the projection's "open-ended"
    /// upper bound. Selecting <see cref="DateTime.MaxValue"/> means the
    /// natural query <c>PeriodStartUtc &lt;= asOf &amp;&amp; asOf &lt;
    /// PeriodEndUtc</c> works without a NULL-aware branch.
    /// </summary>
    public static readonly DateTime OpenEndedSentinel = DateTime.MaxValue;

    /// <summary>
    /// Source row contributed to the boundary algorithm. One row per
    /// supersession entry; <see cref="ValidToUtc"/> = <c>null</c> represents
    /// an open-ended row that the algorithm materialises against
    /// <see cref="OpenEndedSentinel"/>.
    /// </summary>
    /// <param name="SourceId">
    /// Internal id of the source row — only used to break ties when two
    /// equally-recent rows cover the same slice midpoint. Ascending id wins.
    /// </param>
    /// <param name="FieldName">
    /// Stable name of the field this source row contributes. Multiple rows
    /// for the same field with overlapping intervals are tolerated; the
    /// tie-break rule decides which one wins per slice.
    /// </param>
    /// <param name="Value">
    /// The resolved value. May be <c>null</c> when the source field is
    /// itself nullable.
    /// </param>
    /// <param name="ValidFromUtc">UTC instant at which the source row becomes effective.</param>
    /// <param name="ValidToUtc">
    /// UTC instant at which the source row was superseded. <c>null</c>
    /// represents an open-ended (current) row.
    /// </param>
    /// <param name="CreatedAtUtc">
    /// UTC creation timestamp from the source row. Drives the most-recent
    /// tie-break when multiple rows cover the same slice midpoint.
    /// </param>
    public sealed record SourceRow(
        long SourceId,
        string FieldName,
        object? Value,
        DateTime ValidFromUtc,
        DateTime? ValidToUtc,
        DateTime CreatedAtUtc);

    /// <summary>
    /// Flattens the supplied source rows into a chronologically-ordered
    /// disjoint slice list. See the algorithm under the type-level remarks.
    /// </summary>
    /// <param name="sourceRows">
    /// Source rows from every contributing supersession table, flattened into
    /// a single list. The list may contain rows for any number of distinct
    /// fields; the algorithm groups by <see cref="SourceRow.FieldName"/>
    /// internally.
    /// </param>
    /// <param name="fieldNames">
    /// Closed set of field names the caller expects to find in every slice.
    /// Every emitted slice's <c>ResolvedFields</c> dictionary contains a key
    /// for every entry — missing fields resolve to <c>null</c>. The set is
    /// supplied separately from the source rows so that absent fields
    /// (e.g. a contributor with no contacts on file) still produce an entry
    /// in every slice for downstream schema stability.
    /// </param>
    /// <returns>
    /// Chronologically-ordered list of disjoint
    /// <see cref="PeriodSlice{TSource}"/>. An all-null slice (no source row
    /// covered the slice midpoint for any of <paramref name="fieldNames"/>)
    /// is suppressed; the rest are emitted verbatim.
    /// </returns>
    public static IReadOnlyList<PeriodSlice<object>> Build(
        IReadOnlyCollection<SourceRow> sourceRows,
        IReadOnlyCollection<string> fieldNames)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        ArgumentNullException.ThrowIfNull(fieldNames);

        if (sourceRows.Count == 0 || fieldNames.Count == 0)
        {
            return Array.Empty<PeriodSlice<object>>();
        }

        // Boundary set: every ValidFromUtc + (ValidToUtc ?? Sentinel) across rows.
        var boundaries = new SortedSet<DateTime>();
        foreach (var row in sourceRows)
        {
            boundaries.Add(row.ValidFromUtc);
            boundaries.Add(row.ValidToUtc ?? OpenEndedSentinel);
        }

        if (boundaries.Count < 2)
        {
            // Single boundary -> no slices can form. The defensive guard
            // covers degenerate input (every row collapsed to the same instant).
            return Array.Empty<PeriodSlice<object>>();
        }

        var ordered = boundaries.ToList();
        var slices = new List<PeriodSlice<object>>(ordered.Count - 1);

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var start = ordered[i];
            var end = ordered[i + 1];

            // Sentinel slices: skip degenerate zero-width windows that can
            // arise from coincident boundary timestamps.
            if (end <= start)
            {
                continue;
            }

            var midpoint = ComputeMidpoint(start, end);
            var resolved = new Dictionary<string, object?>(fieldNames.Count, StringComparer.Ordinal);

            foreach (var fieldName in fieldNames)
            {
                resolved[fieldName] = ResolveFieldAtInstant(sourceRows, fieldName, midpoint);
            }

            // Suppress slices where every field is null — these arise between
            // distant source rows and add no information to downstream reports.
            if (resolved.Values.All(v => v is null))
            {
                continue;
            }

            slices.Add(new PeriodSlice<object>(start, end, resolved));
        }

        return slices;
    }

    /// <summary>
    /// Resolves the value of <paramref name="fieldName"/> at the
    /// <paramref name="instant"/> by scanning <paramref name="sourceRows"/>.
    /// Returns <c>null</c> when no row's validity interval covers the instant
    /// for the field. The tie-break rule is "most recently created wins; on
    /// equal creation timestamps the ascending source id wins".
    /// </summary>
    /// <param name="sourceRows">All candidate source rows.</param>
    /// <param name="fieldName">Field to resolve.</param>
    /// <param name="instant">The instant within the candidate slice.</param>
    /// <returns>The resolved value, or <c>null</c> when none qualifies.</returns>
    private static object? ResolveFieldAtInstant(
        IReadOnlyCollection<SourceRow> sourceRows,
        string fieldName,
        DateTime instant)
    {
        SourceRow? winner = null;

        foreach (var row in sourceRows)
        {
            if (!string.Equals(row.FieldName, fieldName, StringComparison.Ordinal))
            {
                continue;
            }

            var upper = row.ValidToUtc ?? OpenEndedSentinel;
            // Half-open semantics: [ValidFromUtc, upper).
            if (row.ValidFromUtc > instant || instant >= upper)
            {
                continue;
            }

            if (winner is null
                || row.CreatedAtUtc > winner.CreatedAtUtc
                || (row.CreatedAtUtc == winner.CreatedAtUtc && row.SourceId < winner.SourceId))
            {
                winner = row;
            }
        }

        return winner?.Value;
    }

    /// <summary>
    /// Returns a representative instant strictly inside <c>[start, end)</c>.
    /// The midpoint is the natural choice; for the open-ended sentinel slice
    /// we fall back to one tick past the start to avoid arithmetic overflow.
    /// </summary>
    /// <param name="start">Inclusive slice start (UTC).</param>
    /// <param name="end">Exclusive slice end (UTC, possibly <see cref="DateTime.MaxValue"/>).</param>
    /// <returns>An instant inside the slice.</returns>
    private static DateTime ComputeMidpoint(DateTime start, DateTime end)
    {
        // For the open-ended slice we just need any instant strictly past the
        // start — the upper bound is unbounded. Adding 1 tick keeps the math
        // simple and is sufficient for half-open interval matching.
        if (end == OpenEndedSentinel)
        {
            return start.AddTicks(1);
        }

        var halfSpan = (end - start).Ticks / 2;
        return start.AddTicks(halfSpan);
    }
}
