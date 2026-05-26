using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0536 / TOR CF 04.09 — produces one KPI tile per
/// <see cref="ApplicationStatus"/> bucket the caller has at least one application
/// in (Solicitant-scoped histogram). Each emitted <see cref="KpiWidget"/> carries
/// the status name embedded in its <see cref="KpiWidget.Code"/> (prefix +
/// upper-cased status name) so the React layer can render the small per-status
/// histogram described in CF 04.09 ("by status" breakdown).
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty-data semantics.</b> Returns an empty list (no tiles) when the caller
/// has no applications at all — the dashboard shell renders a "no requests yet"
/// placeholder rather than a flat-zero histogram. This mirrors how the existing
/// inbox / approval-queue tiles degrade when there is no data.
/// </para>
/// <para>
/// <b>Stable codes.</b> The <see cref="WidgetCodePrefix"/> + status-name pattern is
/// deterministic; integration tests pin the keys so future enum-name changes
/// surface as a regression (status names are part of the wire shape per the
/// <c>Cnas.Ps.Contracts</c> DTO surface).
/// </para>
/// </remarks>
public sealed class MyRequestsByStatusKpiProducer(
    IReadOnlyCnasDbContext db) : IDashboardTileProducer
{
    /// <summary>
    /// Code prefix used for every histogram bucket. The full code is
    /// <c>{Prefix}_{STATUS_NAME}</c> (e.g. <c>MY_REQUESTS_STATUS_UNDEREXAMINATION</c>).
    /// </summary>
    public const string WidgetCodePrefix = "MY_REQUESTS_STATUS";

    /// <summary>Deep-link target rendered on each histogram bucket (R0534).</summary>
    public const string DeepLink = "/profile/me/applications";

    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public DashboardCategory Category => DashboardCategory.WorkflowUpdates;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Group + count in a single query — projects to {Status, Count} so the in-memory
        // step that builds the KpiWidget list operates on a tiny rowset.
        var rows = await _db.Applications
            .Where(a => a.IsActive && a.SolicitantId == userId)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var widgets = new List<KpiWidget>(rows.Count);
        foreach (var row in rows)
        {
            // Status name embedded in the code; OrdinalIgnoreCase + InvariantCulture
            // upper-case avoids the Turkish-i pitfall on the wire.
            var statusName = row.Status.ToString().ToUpperInvariant();
            widgets.Add(new KpiWidget(
                Code: $"{WidgetCodePrefix}_{statusName}",
                Title: $"Cereri — {row.Status}",
                Value: row.Count,
                Unit: "cereri",
                Category: nameof(DashboardCategory.WorkflowUpdates),
                DeepLinkUrl: DeepLink));
        }

        IReadOnlyList<KpiWidget> result = widgets;
        return Result<IReadOnlyList<KpiWidget>>.Success(result);
    }
}
