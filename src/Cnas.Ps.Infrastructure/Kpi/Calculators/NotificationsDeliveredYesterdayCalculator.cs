using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Kpi.Calculators;

/// <summary>
/// R0201 / TOR CF 20.02 — computes <c>Notifications.DeliveredYesterday</c>:
/// count of <see cref="Notification"/> rows whose
/// <see cref="Notification.DeliveryStatus"/> is
/// <see cref="NotificationDeliveryStatus.Delivered"/> AND whose
/// <see cref="AuditableEntity.CreatedAtUtc"/> falls inside the snapshot-date
/// SI day [00:00 UTC, 24:00 UTC).
/// </summary>
/// <remarks>
/// Uses <c>CreatedAtUtc</c> rather than <c>DispatchedAtUtc</c> as the
/// time-bucket anchor because every row carries the former (mandatory)
/// while the latter is only stamped on successful delivery; bucketing on
/// the always-populated column keeps the daily totals consistent with the
/// operator dashboard's "rows received yesterday" tile.
/// </remarks>
/// <param name="db">Read-only replica context resolved per fire.</param>
public sealed class NotificationsDeliveredYesterdayCalculator(IReadOnlyCnasDbContext db) : IKpiCalculator
{
    private readonly IReadOnlyCnasDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public string KpiCode => "Notifications.DeliveredYesterday";

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        var dayStart = snapshotDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        var count = await _db.Notifications
            .Where(n => n.IsActive
                && n.DeliveryStatus == NotificationDeliveryStatus.Delivered
                && n.CreatedAtUtc >= dayStart
                && n.CreatedAtUtc < dayEnd)
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
