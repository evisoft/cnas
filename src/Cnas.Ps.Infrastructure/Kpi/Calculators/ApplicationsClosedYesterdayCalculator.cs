using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Kpi.Calculators;

/// <summary>
/// R0201 / TOR CF 20.02 — computes <c>Applications.ClosedYesterday</c>: count
/// of <see cref="ServiceApplication"/> rows whose
/// <see cref="ServiceApplication.Status"/> is
/// <see cref="ApplicationStatus.Closed"/> AND whose
/// <see cref="AuditableEntity.UpdatedAtUtc"/> falls inside the snapshot-date
/// SI day [00:00 UTC, 24:00 UTC).
/// </summary>
/// <remarks>
/// The KPI's name uses "Yesterday" because the daily Quartz job runs at
/// 02:00 UTC with <c>snapshotDate = today - 1</c>; the operator's "yesterday"
/// view is the most recent fully-elapsed UTC day. Using <c>UpdatedAtUtc</c>
/// as the boundary is a pragmatic proxy — <c>ClosedAtUtc</c> is only set on
/// final-decision closes (not on Rejected / Withdrawn), so the audit-table
/// timestamp on the parent row is the more consistent signal.
/// </remarks>
/// <param name="db">Read-only replica context resolved per fire.</param>
public sealed class ApplicationsClosedYesterdayCalculator(IReadOnlyCnasDbContext db) : IKpiCalculator
{
    private readonly IReadOnlyCnasDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public string KpiCode => "Applications.ClosedYesterday";

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        // UTC day boundaries — start inclusive, end exclusive.
        var dayStart = snapshotDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        var count = await _db.Applications
            .Where(a => a.IsActive
                && a.Status == ApplicationStatus.Closed
                && a.UpdatedAtUtc != null
                && a.UpdatedAtUtc >= dayStart
                && a.UpdatedAtUtc < dayEnd)
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
