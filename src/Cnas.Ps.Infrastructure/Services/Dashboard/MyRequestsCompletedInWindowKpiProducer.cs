using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0536 / TOR CF 04.09 — produces the "my requests completed in window" KPI tile.
/// Counts the caller's applications closed within the rolling
/// <see cref="WindowDays"/> (default 30) days, regardless of terminal status
/// (Closed, Rejected, Withdrawn — anything that stamped
/// <c>ServiceApplication.ClosedAtUtc</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Window contract.</b> The window is the inclusive last-N-days range computed
/// from <see cref="ICnasTimeProvider.UtcNow"/> minus <see cref="WindowDays"/>. The
/// constant is process-static so a test harness can pin the count deterministically;
/// raising the window is a single-line change AND a coordinated UX change (the
/// citizen page renders the same window).
/// </para>
/// <para>
/// <b>Lifetime / contract.</b> Scoped — captures the per-request read DbContext +
/// the injected clock so the rolling window is deterministic under the test
/// harness. Returns a zero-value widget when no closures fall inside the window.
/// </para>
/// </remarks>
public sealed class MyRequestsCompletedInWindowKpiProducer(
    IReadOnlyCnasDbContext db,
    ICnasTimeProvider clock) : IDashboardTileProducer
{
    /// <summary>Stable widget code consumed by tests and the React key on the UI.</summary>
    public const string WidgetCode = "MY_REQUESTS_COMPLETED_IN_WINDOW";

    /// <summary>Deep-link target rendered on the tile (R0534).</summary>
    public const string DeepLink = "/profile/me/applications";

    /// <summary>
    /// Size (in days) of the rolling completion window. 30 mirrors the
    /// MissingDocsSlaJob's 30-day reference window so the citizen sees the same
    /// reference period across the dashboard and the dossier-status indicators.
    /// </summary>
    public const int WindowDays = 30;

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
        var threshold = _clock.UtcNow.AddDays(-WindowDays);
        var count = await _db.Applications
            .Where(a => a.IsActive
                        && a.SolicitantId == userId
                        && a.ClosedAtUtc != null
                        && a.ClosedAtUtc >= threshold)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: WidgetCode,
                Title: $"Cereri procesate (ultimele {WindowDays} zile)",
                Value: count,
                Unit: "cereri",
                Category: nameof(DashboardCategory.WorkflowUpdates),
                DeepLinkUrl: DeepLink),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
