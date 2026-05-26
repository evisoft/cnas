namespace Cnas.Ps.Application.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — produces a list of <see cref="KpiSnapshotEntry"/>
/// rows for a single KPI on a given snapshot date. One implementation per KPI;
/// implementations are discovered by DI and invoked by the
/// <see cref="IKpiSnapshotService"/> orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plurality.</b> A calculator may emit zero, one, or many entries — zero
/// when the KPI has no observable data on the date (the orchestrator records
/// nothing in that case), one when the KPI is a scalar (no facets), and many
/// when the KPI is faceted on <see cref="KpiSnapshotEntry.Dimension1"/> /
/// <see cref="KpiSnapshotEntry.Dimension2"/>. The facet keys MUST be stable
/// (e.g. classifier codes) — the rolled-up rows compare across days only
/// when the facet keys match exactly.
/// </para>
/// <para>
/// <b>Determinism.</b> A calculator MUST be a pure function of the snapshot
/// date and the persisted state visible to its injected
/// <c>IReadOnlyCnasDbContext</c>. Re-running for the same date on the same
/// data MUST produce identical entries — this is what makes the snapshot
/// upsert idempotent.
/// </para>
/// <para>
/// <b>Read-only.</b> Calculators read; they never write. The orchestrator
/// owns the write side and persists the entries through <c>ICnasDbContext</c>.
/// </para>
/// </remarks>
public interface IKpiCalculator
{
    /// <summary>
    /// Stable code under which the calculator's entries are recorded
    /// (e.g. <c>"Applications.Pending"</c>). Each emitted entry's
    /// <see cref="KpiSnapshotEntry.KpiCode"/> MUST equal this value — the
    /// orchestrator asserts the invariant before persisting.
    /// </summary>
    string KpiCode { get; }

    /// <summary>
    /// Computes the KPI for <paramref name="snapshotDate"/>. Returns an
    /// empty list when the KPI has no observation for the day.
    /// </summary>
    /// <param name="snapshotDate">
    /// UTC calendar date the snapshot is being computed for. Time-bounded
    /// KPIs interpret it as the SI day [00:00 UTC, 24:00 UTC).
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The (possibly empty) list of entries for this KPI on the date.</returns>
    Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R0201 — a single value emitted by an <see cref="IKpiCalculator"/>.
/// Immutable. The orchestrator upserts each entry against the natural key
/// (<see cref="Cnas.Ps.Core.Domain.KpiSnapshot.SnapshotDate"/>,
/// <see cref="KpiCode"/>, <see cref="Dimension1"/>, <see cref="Dimension2"/>).
/// </summary>
/// <param name="KpiCode">
/// Stable KPI code; MUST equal the producing calculator's
/// <see cref="IKpiCalculator.KpiCode"/>.
/// </param>
/// <param name="Value">
/// The computed value. Always a non-negative decimal; counts use integer
/// values, averages and percentages carry fractional digits.
/// </param>
/// <param name="ValueUnit">
/// Unit of measure — one of the well-known
/// <see cref="Cnas.Ps.Core.Domain.KpiValueUnits"/> constants.
/// </param>
/// <param name="Dimension1">
/// Optional facet key (region, classifier code, ...) — <see cref="string.Empty"/>
/// when unused. NEVER <c>null</c>: the unique index treats <c>NULL</c> as
/// distinct from <c>NULL</c>, which would let duplicates slip through.
/// </param>
/// <param name="Dimension2">
/// Optional second facet key, same null-aversion contract as
/// <paramref name="Dimension1"/>.
/// </param>
public sealed record KpiSnapshotEntry(
    string KpiCode,
    decimal Value,
    string ValueUnit,
    string Dimension1,
    string Dimension2);
