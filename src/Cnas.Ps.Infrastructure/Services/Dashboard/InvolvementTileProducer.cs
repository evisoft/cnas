using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0531 / CF 04.02 — produces the
/// <see cref="DashboardCategory.ItemsRequiringInvolvement"/> dashboard tile. Counts
/// <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.AssignedUserId"/>
/// matches the caller AND whose <see cref="WorkflowTask.Status"/> is
/// <see cref="WorkflowTaskStatus.InProgress"/> (i.e. tasks the caller is actively
/// holding and that need the caller's input to advance).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from TaskArrivals.</b> "Task arrivals" counts pending (not-yet-claimed)
/// tasks; this tile counts already-claimed-and-in-progress tasks. The split mirrors
/// CF 04.02 — "items requiring involvement" is the operational backlog of work the
/// caller has accepted but not finished.
/// </para>
/// <para>
/// <b>Read-only / clock-injected.</b> Same Scoped-with-read-DbContext + injected
/// <see cref="ICnasTimeProvider"/> pattern as the sibling producers; the clock is
/// not actually consumed today but is reserved so the tile can add an "overdue
/// only" gate without a signature change.
/// </para>
/// </remarks>
public sealed class InvolvementTileProducer(
    IReadOnlyCnasDbContext db,
    ICnasTimeProvider clock) : IDashboardTileProducer
{
    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;
    // Reserved for a future "overdue items requiring involvement" sub-tile.
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public DashboardCategory Category => DashboardCategory.ItemsRequiringInvolvement;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        _ = _clock; // reserved — see <remarks>.

        var count = await _db.WorkflowTasks
            .Where(t => t.IsActive
                        && t.AssignedUserId == userId
                        && t.Status == WorkflowTaskStatus.InProgress)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: "INVOLVEMENT_ITEMS",
                Title: "Sarcini în lucru",
                Value: count,
                Unit: "sarcini",
                Category: nameof(DashboardCategory.ItemsRequiringInvolvement),
                DeepLinkUrl: "/inbox"),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
