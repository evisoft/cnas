namespace Cnas.Ps.Contracts;

/// <summary>
/// R0201 / TOR CF 20.02 — public projection of a single
/// <c>Cnas.Ps.Core.Domain.KpiSnapshot</c> row, served by the dashboard read
/// endpoints. All facet fields are non-nullable strings (empty string when
/// the calculator does not facet the KPI) so JSON clients can rely on the
/// field being present.
/// </summary>
/// <param name="SnapshotDate">
/// UTC calendar date the row was computed for. ISO-8601 date in JSON.
/// </param>
/// <param name="KpiCode">
/// Stable KPI code, e.g. <c>"Applications.Pending"</c>. Stable across deployments.
/// </param>
/// <param name="Value">
/// Computed value. Always a non-negative decimal — counts use integer values,
/// averages and percentages carry fractional digits.
/// </param>
/// <param name="ValueUnit">
/// Unit of measure — one of <c>"count"</c> | <c>"days"</c> | <c>"hours"</c> |
/// <c>"percent"</c> | <c>"ratio"</c> per
/// <c>Cnas.Ps.Core.Domain.KpiValueUnits</c>. The dashboard chooses the
/// renderer (bar chart, gauge, ...) from this field.
/// </param>
/// <param name="Dimension1">
/// First facet key. Empty string when the calculator does not facet on this
/// dimension. NEVER <c>null</c> — see the entity remarks for the rationale.
/// </param>
/// <param name="Dimension2">
/// Second facet key. Empty string when unused, same non-null contract as
/// <paramref name="Dimension1"/>.
/// </param>
public sealed record KpiSnapshotDto(
    DateOnly SnapshotDate,
    string KpiCode,
    decimal Value,
    string ValueUnit,
    string Dimension1,
    string Dimension2);

/// <summary>
/// R0201 / TOR CF 20.02 — summary of a single
/// <c>IKpiSnapshotService.RunForDateAsync</c> invocation. Returned by both
/// the Quartz job (via logs / audit) and the admin
/// <c>POST /api/kpi/snapshots/run</c> endpoint.
/// </summary>
/// <param name="Id">
/// Sqid-encoded run identifier. Deterministic per (snapshot date, fire
/// instance) — the service derives a stable surrogate so the audit row and
/// the HTTP response carry the same opaque token operators can correlate.
/// </param>
/// <param name="SnapshotDate">UTC calendar date the run computed.</param>
/// <param name="CalculatorsRun">
/// Count of <c>IKpiCalculator</c> implementations invoked during the run.
/// Equal to <c>IServiceProvider.GetServices&lt;IKpiCalculator&gt;().Count()</c>
/// at registration time.
/// </param>
/// <param name="RowsUpserted">
/// Count of <c>KpiSnapshot</c> rows the run persisted (inserts + updates).
/// May exceed <paramref name="CalculatorsRun"/> when calculators emit
/// per-facet rows.
/// </param>
/// <param name="DurationMs">
/// Wall-clock duration of the run, in whole milliseconds, including the
/// final <c>SaveChangesAsync</c>. Surfaced primarily for operator
/// observability — a sustained creep is the canary for a calculator that
/// needs query tuning.
/// </param>
public sealed record KpiSnapshotRunDto(
    string Id,
    DateOnly SnapshotDate,
    int CalculatorsRun,
    int RowsUpserted,
    long DurationMs);
