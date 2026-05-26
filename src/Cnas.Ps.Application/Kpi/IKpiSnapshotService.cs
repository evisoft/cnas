using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — orchestrator over the registered
/// <see cref="IKpiCalculator"/> set + read facade for the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run side.</b> <see cref="RunForDateAsync"/> invokes every registered
/// calculator, persists each emitted <see cref="KpiSnapshotEntry"/> via
/// upsert on the natural key, and writes a single
/// <c>KPI.SNAPSHOT.COMPLETED</c> audit Information row carrying the run
/// counters. Idempotent — re-running for the same date overwrites the
/// previous values in place.
/// </para>
/// <para>
/// <b>Read side.</b> <see cref="QueryAsync"/> and
/// <see cref="GetLatestAsync"/> are the dashboard's only DB touch points;
/// neither scans the OLTP tables.
/// </para>
/// </remarks>
public interface IKpiSnapshotService
{
    /// <summary>
    /// Invokes every registered calculator for <paramref name="snapshotDate"/>
    /// and upserts the emitted entries into the snapshot store. Always
    /// succeeds when the calculators succeed — failures from a single
    /// calculator are logged and the run continues with the next, so a
    /// broken calculator cannot stall the dashboard pipeline.
    /// </summary>
    /// <param name="snapshotDate">UTC calendar date the snapshot is computed for.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// A success <see cref="Result{T}"/> carrying the
    /// <see cref="KpiSnapshotRunDto"/> summary on completion.
    /// </returns>
    Task<Result<KpiSnapshotRunDto>> RunForDateAsync(
        DateOnly snapshotDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read API for the dashboard. Returns every snapshot row whose
    /// <see cref="Cnas.Ps.Core.Domain.KpiSnapshot.SnapshotDate"/> falls in
    /// the inclusive range [<paramref name="fromDate"/>,
    /// <paramref name="toDate"/>], optionally filtered to a single
    /// <paramref name="kpiCodeFilter"/>. Sorted
    /// (<c>SnapshotDate DESC, KpiCode ASC</c>).
    /// </summary>
    /// <param name="fromDate">Inclusive lower bound on the snapshot date.</param>
    /// <param name="toDate">Inclusive upper bound on the snapshot date.</param>
    /// <param name="kpiCodeFilter">
    /// Optional KPI-code filter. When non-null and non-empty, restricts the
    /// result to rows whose
    /// <see cref="Cnas.Ps.Core.Domain.KpiSnapshot.KpiCode"/> matches verbatim.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>Read-only list of DTOs in the documented sort order.</returns>
    Task<IReadOnlyList<KpiSnapshotDto>> QueryAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string? kpiCodeFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent value (highest <c>SnapshotDate</c>) for each
    /// requested KPI code. Sum-aggregated across dimensions so faceted KPIs
    /// surface as a single scalar — the dashboard's "today" tiles want one
    /// number per KPI, not a breakdown. Returns an empty entry for any code
    /// not yet snapshotted.
    /// </summary>
    /// <param name="kpiCodes">
    /// Set of KPI codes to fetch. Duplicates are deduplicated; an empty
    /// input yields an empty result.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// Dictionary keyed by KPI code carrying the summed value. KPI codes not
    /// present in the store are omitted from the dictionary.
    /// </returns>
    Task<IReadOnlyDictionary<string, decimal>> GetLatestAsync(
        IReadOnlyCollection<string> kpiCodes,
        CancellationToken cancellationToken = default);
}
