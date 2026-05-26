using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0533 / TOR CF 04.04 — KPI grid cell counting <see cref="WorkflowTask"/> rows assigned
/// to the calling user whose <c>DueAtUtc</c> has elapsed (i.e. the SLA deadline has
/// passed). Models the "Overdue tasks" KPI on the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time discipline.</b> The "overdue" window pivots on
/// <see cref="ICnasTimeProvider.UtcNow"/>; we never call <c>DateTime.UtcNow</c>
/// directly per CLAUDE.md cross-cutting + the iter-115 acceptance gate.
/// </para>
/// <para>
/// <b>Deep-link.</b> Cell drills into the caller's task inbox at <c>/inbox</c> — the
/// inbox renders the same rows ordered by <c>DueAtUtc</c> ASC (R0543) so the overdue
/// items float to the top.
/// </para>
/// </remarks>
public sealed class OverdueTasksKpiGridProducer(
    IReadOnlyCnasDbContext db,
    ICnasTimeProvider clock) : IKpiGridProducer
{
    /// <summary>Stable KPI cell code.</summary>
    public const string CellCode = "OVERDUE_TASKS";

    /// <summary>Pre-allocated wildcard role list.</summary>
    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiGridCellDto>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var count = await _db.WorkflowTasks
            .Where(t => t.IsActive
                        && t.AssignedUserId == userId
                        && t.DueAtUtc != null
                        && t.DueAtUtc < now
                        && t.Status != WorkflowTaskStatus.Completed
                        && t.Status != WorkflowTaskStatus.Cancelled)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiGridCellDto> cells =
        [
            new KpiGridCellDto(
                Code: CellCode,
                Title: "Sarcini cu termen expirat",
                Value: count,
                Trend: null,
                DeepLinkUrl: "/inbox"),
        ];
        return Result<IReadOnlyList<KpiGridCellDto>>.Success(cells);
    }
}
