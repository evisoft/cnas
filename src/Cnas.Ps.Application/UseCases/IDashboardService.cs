using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC04 — Use dashboard. Per role; KPIs are configurable (FLEX 003 / FLEX 004).</summary>
public interface IDashboardService
{
    /// <summary>Returns KPI widgets configured for the calling user.</summary>
    Task<Result<IReadOnlyList<KpiWidget>>> GetWidgetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// R0533 / TOR CF 04.04 — returns the full dashboard snapshot for the calling user:
    /// the legacy per-category widget list AND the aggregate KPI grid cells. The two
    /// projections share the same role-gating envelope but use independent producer
    /// strategies under the hood.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the combined snapshot.</returns>
    Task<Result<DashboardSnapshotDto>> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
