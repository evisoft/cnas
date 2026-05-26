using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0533 / TOR CF 04.04 — KPI grid cell counting the calling user's unread, active
/// notifications. Mirrors <see cref="UnreadNotificationsTileProducer"/> in semantics
/// but emits a <see cref="KpiGridCellDto"/> with a list-page deep-link (the
/// /inbox route) so the cell is clickable per R0534.
/// </summary>
public sealed class UnreadNotificationsKpiGridProducer(
    IReadOnlyCnasDbContext db) : IKpiGridProducer
{
    /// <summary>Stable KPI cell code.</summary>
    public const string CellCode = "UNREAD_NOTIFICATIONS";

    /// <summary>Pre-allocated wildcard role list.</summary>
    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiGridCellDto>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var count = await _db.Notifications
            .Where(n => n.IsActive
                        && n.RecipientUserId == userId
                        && n.ReadAtUtc == null)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiGridCellDto> cells =
        [
            new KpiGridCellDto(
                Code: CellCode,
                Title: "Notificări necitite",
                Value: count,
                Trend: null,
                DeepLinkUrl: "/inbox"),
        ];
        return Result<IReadOnlyList<KpiGridCellDto>>.Success(cells);
    }
}
