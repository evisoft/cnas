using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0536 / TOR CF 04.09 — produces the "my requests in examination" KPI tile.
/// Counts <see cref="ServiceApplication"/> rows owned by the calling Solicitant
/// (<see cref="ServiceApplication.SolicitantId"/> equals the caller's user id —
/// the user-id-equals-solicitant-id convention is honoured project-wide, see
/// <see cref="Cnas.Ps.Infrastructure.Services.ApplicationServiceImpl.MineAsync"/>)
/// whose status is <see cref="ApplicationStatus.UnderExamination"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Category.</b> The tile lives in the <see cref="DashboardCategory.WorkflowUpdates"/>
/// bucket per CF 04.02 — it reflects in-flight progress on the citizen's own
/// requests, not approval-queue depth or notification counts.
/// </para>
/// <para>
/// <b>Lifetime / contract.</b> Scoped consumer of <see cref="IReadOnlyCnasDbContext"/>
/// — the tile reads the replica per ARH 025 / R0026. The producer NEVER throws on
/// a missing precondition (no applications, anonymous principal, …); it returns a
/// zero-value widget instead and the dashboard service surfaces it.
/// </para>
/// <para>
/// <b>Deep-link.</b> Points at the citizen's self-service "my applications" page
/// (<c>/profile/me/applications</c>) so a tile click drills directly into the
/// underlying record list (R0534 / CF 04.05-06).
/// </para>
/// </remarks>
public sealed class MyRequestsInExaminationKpiProducer(
    IReadOnlyCnasDbContext db) : IDashboardTileProducer
{
    /// <summary>Stable widget code consumed by tests and the React key on the UI.</summary>
    public const string WidgetCode = "MY_REQUESTS_IN_EXAMINATION";

    /// <summary>Deep-link target rendered on the tile (R0534).</summary>
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
        var count = await _db.Applications
            .Where(a => a.IsActive
                        && a.SolicitantId == userId
                        && a.Status == ApplicationStatus.UnderExamination)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: WidgetCode,
                Title: "Cereri în examinare",
                Value: count,
                Unit: "cereri",
                Category: nameof(DashboardCategory.WorkflowUpdates),
                DeepLinkUrl: DeepLink),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
