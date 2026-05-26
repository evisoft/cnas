using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0531 / CF 04.02 — produces the <see cref="DashboardCategory.WorkflowUpdates"/>
/// dashboard tile. Counts <see cref="WorkflowTaskStepHistory"/> rows in the rolling
/// 24-hour window that touch a task the caller is or was assigned to, but for which
/// the actor was NOT the caller (i.e. someone else moved one of the caller's
/// workflows forward).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only.</b> The producer consumes <see cref="IReadOnlyCnasDbContext"/> so
/// the streaming-replication replica routing kicks in per ARH 025 / R0026 — the tile
/// must not steal write-side bandwidth from the primary backend.
/// </para>
/// <para>
/// <b>Time discipline.</b> The 24-hour window pivots on
/// <c>ICnasTimeProvider.UtcNow</c>; we NEVER call <c>DateTime.UtcNow</c> directly
/// (CLAUDE.md cross-cutting + iter-115 acceptance gate).
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request read DbContext.
/// </para>
/// </remarks>
public sealed class WorkflowUpdatesTileProducer(
    IReadOnlyCnasDbContext db,
    ICnasTimeProvider clock) : IDashboardTileProducer
{
    /// <summary>Rolling window used to define "recent" workflow updates.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public DashboardCategory Category => DashboardCategory.WorkflowUpdates;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var since = _clock.UtcNow - Window;

        // Step-history rows where someone OTHER than the caller acted on a task the
        // caller is currently or previously assigned to. The IsActive filter on tasks
        // matches the soft-delete convention shared by every per-domain projector.
        var query = from h in _db.WorkflowTaskStepHistories
                    where h.OccurredAt >= since
                          && (h.ActorUserId == null || h.ActorUserId != userId)
                    join t in _db.WorkflowTasks on h.WorkflowTaskId equals t.Id
                    where t.IsActive
                          && (t.AssignedUserId == userId || t.OriginalAssigneeUserId == userId)
                    select h.Id;

        var count = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: "WORKFLOW_UPDATES_LAST24H",
                Title: "Actualizări flux (24h)",
                Value: count,
                Unit: "actualizări",
                Category: nameof(DashboardCategory.WorkflowUpdates),
                DeepLinkUrl: "/inbox"),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
