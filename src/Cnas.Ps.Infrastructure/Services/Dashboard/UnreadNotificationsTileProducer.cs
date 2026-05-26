using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0170 / TOR CF 22.02 + CF 04.02 — produces the
/// <see cref="DashboardCategory.SystemNotifications"/> dashboard tile. Counts
/// every <see cref="Notification"/> row addressed to the calling user that is
/// still unread (<see cref="Notification.ReadAtUtc"/> is <c>null</c>) and
/// active. The single emitted widget surfaces on the citizen dashboard as the
/// "unread notifications" KPI tile — pairing the persistent inbox list
/// (<c>/inbox</c>) with the dashboard tile + toast container required by CF
/// 22.02 (notifications must appear on the dashboard, not only in the inbox).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only.</b> The producer consumes <see cref="IReadOnlyCnasDbContext"/>
/// so the replica routing kicks in per ARH 025 / R0026 — the tile must not
/// steal write-side bandwidth from the primary backend.
/// </para>
/// <para>
/// <b>Channel filter.</b> The unread count includes EVERY channel
/// (<see cref="NotificationChannel.InApp"/> /
/// <see cref="NotificationChannel.Email"/> /
/// <see cref="NotificationChannel.Sms"/>) so a citizen who reads their email
/// but never opens the in-app inbox still sees a clean tile. The
/// <see cref="Notification.ReadAtUtc"/> column is the canonical "seen" flag
/// across surfaces.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request read DbContext.
/// </para>
/// </remarks>
public sealed class UnreadNotificationsTileProducer(
    IReadOnlyCnasDbContext db) : IDashboardTileProducer
{
    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public DashboardCategory Category => DashboardCategory.SystemNotifications;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var count = await _db.Notifications
            .Where(n => n.IsActive
                        && n.RecipientUserId == userId
                        && n.ReadAtUtc == null)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: "NOTIFICATIONS_UNREAD",
                Title: "Notificări necitite",
                Value: count,
                Unit: "notificări",
                Category: nameof(DashboardCategory.SystemNotifications),
                DeepLinkUrl: "/inbox"),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
