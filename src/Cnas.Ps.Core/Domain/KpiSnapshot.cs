namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0201 / TOR CF 20.02 — pre-aggregated KPI snapshot row. One row per
/// (<see cref="SnapshotDate"/>, <see cref="KpiCode"/>, <see cref="Dimension1"/>,
/// <see cref="Dimension2"/>) tuple, written daily by the
/// <c>KpiSnapshotJob</c> and read by the operator-dashboard endpoint without
/// scanning the OLTP tables on every render.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why snapshots.</b> The dashboard's natural query shape is "count rows in
/// table X grouped by status / region / service" — a full table scan on every
/// page render does not scale to the registry sizes in production. R0201 says:
/// pre-compute the aggregates once per day into this table; the dashboard
/// reads from the snapshot in O(1).
/// </para>
/// <para>
/// <b>Why decimal.</b> The same surface carries both counts (always integer)
/// and ratios / averages (e.g. average task handling time in hours, or a
/// percentage). Storing everything as <see cref="decimal"/> lets a single
/// table serve every KPI shape without a discriminator column.
/// </para>
/// <para>
/// <b>Why empty-string dimensions.</b> SQL's <c>UNIQUE</c> constraint treats
/// <c>NULL ≠ NULL</c>, so a unique on <c>(SnapshotDate, KpiCode, Dimension1,
/// Dimension2)</c> would allow duplicates when the optional dimensions are
/// absent. The configuration defaults both columns to the empty string so
/// the natural key always uniques as expected; calculators that omit a
/// dimension supply <see cref="string.Empty"/> rather than <c>null</c>.
/// </para>
/// <para>
/// <b>Idempotency.</b> The snapshot service upserts on the natural key — a
/// re-run for the same date overwrites the previous values in place rather
/// than appending duplicates. Operators can therefore re-trigger
/// <c>POST /api/kpi/snapshots/run</c> safely.
/// </para>
/// </remarks>
public sealed class KpiSnapshot : AuditableEntity, IExternalId
{
    /// <summary>
    /// UTC calendar date the snapshot was computed for. All time-bounded
    /// calculators interpret this as the SI day [00:00 UTC, 24:00 UTC). One
    /// row per (date, kpiCode, dim1, dim2) tuple.
    /// </summary>
    public DateOnly SnapshotDate { get; set; }

    /// <summary>
    /// Stable string identifier for the KPI, e.g. <c>"Applications.Pending"</c>
    /// or <c>"Tasks.AvgHandlingHours"</c>. Part of the public contract —
    /// renaming is a breaking change because dashboard widgets reference
    /// codes verbatim.
    /// </summary>
    public required string KpiCode { get; set; }

    /// <summary>
    /// The computed value. Counts use integer-valued decimals; averages and
    /// percentages carry up to four fractional digits per the column
    /// configuration. Always a non-negative number; calculators that cannot
    /// produce a meaningful value emit zero (e.g.
    /// <c>TasksAverageHandlingTimeCalculator</c> emits zero when no tasks
    /// completed in the look-back window).
    /// </summary>
    public decimal Value { get; set; }

    /// <summary>
    /// Optional first facet key (e.g. region code, service-passport code).
    /// Empty string when the calculator does not facet on this dimension —
    /// never <c>null</c>, so the unique index over the natural key behaves
    /// as expected. See remarks on the entity for the rationale.
    /// </summary>
    public string Dimension1 { get; set; } = string.Empty;

    /// <summary>
    /// Optional second facet key. Empty string when unused, same rationale
    /// as <see cref="Dimension1"/>.
    /// </summary>
    public string Dimension2 { get; set; } = string.Empty;

    /// <summary>
    /// Unit of measure for <see cref="Value"/>. Stable string from a small
    /// closed set: <c>"count"</c> | <c>"days"</c> | <c>"hours"</c> |
    /// <c>"percent"</c> | <c>"ratio"</c>. The dashboard chooses the renderer
    /// (bar chart, gauge, ...) by inspecting this column.
    /// </summary>
    public required string ValueUnit { get; set; }
}

/// <summary>
/// R0201 — well-known <see cref="KpiSnapshot.ValueUnit"/> values used by the
/// shipped calculators. Centralised so a typo in a calculator surfaces as a
/// compile error.
/// </summary>
public static class KpiValueUnits
{
    /// <summary>An integer count — number of rows / events.</summary>
    public const string Count = "count";

    /// <summary>A duration measured in hours (calendar hours, decimal).</summary>
    public const string Hours = "hours";

    /// <summary>A duration measured in calendar days (decimal).</summary>
    public const string Days = "days";

    /// <summary>A percentage in the inclusive range [0, 100].</summary>
    public const string Percent = "percent";

    /// <summary>A ratio in the inclusive range [0, 1].</summary>
    public const string Ratio = "ratio";
}
