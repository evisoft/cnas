using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Kpi.Calculators;

/// <summary>
/// R0201 / TOR CF 20.02 — computes <c>Applications.Pending</c>: count of
/// <see cref="ServiceApplication"/> rows whose <see cref="ServiceApplication.Status"/>
/// is one of {<see cref="ApplicationStatus.Submitted"/>,
/// <see cref="ApplicationStatus.UnderExamination"/>,
/// <see cref="ApplicationStatus.PendingApproval"/>} AND which are still
/// <see cref="AuditableEntity.IsActive"/> = <c>true</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>As-of semantics.</b> The calculator reads the LIVE row state — the EF
/// in-memory provider does not support point-in-time queries and Postgres's
/// streaming replica is the data source in production. A row created after
/// the snapshot date will therefore be counted on the day the job runs even
/// if its creation timestamp lies in the future of the snapshot date.
/// This trade-off is acceptable because the "Pending" KPI is meant to track
/// the CURRENT backlog at end-of-day; historical drift across days is the
/// natural meaning of the daily snapshot timeline.
/// </para>
/// <para>
/// <b>Dimensions.</b> Faceting by region or service-passport code is
/// deferred per TOR CF 20.02 (regional drill-down) — see the R0201 task
/// notes for the rationale. The calculator emits a single scalar entry
/// today; a future iteration can replace the single-row emission with a
/// per-facet group-by without changing the orchestrator contract.
/// </para>
/// </remarks>
/// <param name="db">Read-only replica context resolved per fire.</param>
public sealed class ApplicationsPendingCalculator(IReadOnlyCnasDbContext db) : IKpiCalculator
{
    private readonly IReadOnlyCnasDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public string KpiCode => "Applications.Pending";

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        // Single scalar projection — IsActive guard suppresses soft-deleted rows.
        var count = await _db.Applications
            .Where(a => a.IsActive
                && (a.Status == ApplicationStatus.Submitted
                    || a.Status == ApplicationStatus.UnderExamination
                    || a.Status == ApplicationStatus.PendingApproval))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        return new[]
        {
            new KpiSnapshotEntry(
                KpiCode: KpiCode,
                Value: count,
                ValueUnit: KpiValueUnits.Count,
                Dimension1: string.Empty,
                Dimension2: string.Empty),
        };
    }
}
