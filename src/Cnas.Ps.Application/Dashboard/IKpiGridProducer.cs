using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Dashboard;

/// <summary>
/// R0533 / TOR CF 04.04 — strategy that produces one or more
/// <see cref="KpiGridCellDto"/> rows for the aggregate KPI grid rendered alongside
/// the per-category tile producers on the dashboard. Mirrors
/// <see cref="IDashboardTileProducer"/> but emits ungrouped KPI cells (counter +
/// optional trend + optional deep-link) instead of categorised widget tiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>One producer, one or more cells.</b> Each implementation can emit multiple cells
/// in a single call (e.g. a producer that splits "applications by status" into the
/// three Submitted / UnderExamination / PendingApproval slices), but typically owns one
/// canonical cell code.
/// </para>
/// <para>
/// <b>Role gating.</b> The dashboard service filters producers whose
/// <see cref="SupportedRoles"/> intersect the caller's role set BEFORE invoking
/// <see cref="ProduceAsync(long, CancellationToken)"/>. The wildcard <c>"*"</c> means
/// "every authenticated caller".
/// </para>
/// <para>
/// <b>Deep-link wiring (R0534).</b> Cells whose underlying record set has a canonical
/// detail / list page populate <see cref="KpiGridCellDto.DeepLinkUrl"/> via the
/// iter-118 <see cref="Cnas.Ps.Application.Notifications.INotificationDeepLinkResolver"/>
/// (or an analogous list-route resolver) so the Blazor UI renders the cell value as a
/// clickable anchor target.
/// </para>
/// <para>
/// <b>Failure semantics.</b> A producer MUST NOT throw on a missing precondition.
/// Genuine infrastructure failures propagate via <see cref="Result{T}"/>.failure and
/// the composing service surfaces a degraded snapshot rather than failing the whole
/// dashboard.
/// </para>
/// </remarks>
public interface IKpiGridProducer
{
    /// <summary>
    /// Set of role codes (case-insensitive) for which this producer should run. A
    /// single-element list containing <c>"*"</c> means "every authenticated caller".
    /// </summary>
    IReadOnlyCollection<string> SupportedRoles { get; }

    /// <summary>
    /// Computes the KPI grid cells this producer contributes for the supplied caller.
    /// Implementations MUST return an empty list when no rows match (never throw).
    /// </summary>
    /// <param name="userId">Raw internal primary key of the authenticated caller.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A success result wrapping the (possibly empty) list of grid cells.</returns>
    Task<Result<IReadOnlyList<KpiGridCellDto>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
