using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0531 / CF 04.02 — produces the
/// <see cref="DashboardCategory.ItemsAwaitingApproval"/> dashboard tile. Counts the
/// open maker-checker backlog visible to the caller: every
/// <see cref="PendingAdminAction"/> in <see cref="PendingAdminActionStatus.Pending"/>
/// state (active rows only) that the caller did NOT submit themselves
/// (a maker cannot be their own checker — CLAUDE.md §5.4 / SEC 027).
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval substrate.</b> The current production approval queue lives in
/// <see cref="PendingAdminAction"/> (R0058). The future workflow-task approval edge
/// (Approve/Reject decisions on a <see cref="WorkflowTask"/>) is NOT counted here
/// yet — see <c>Tasks.Overdue</c> for the legacy overlap. A future iter can extend
/// this producer to fold workflow approval-step counts into the same tile without a
/// signature change.
/// </para>
/// <para>
/// <b>Role gating.</b> Approval is by design a decider / admin activity. Producers
/// declare the role allow-list themselves so the dashboard service's role merge
/// matches the deny-by-default semantics enforced by the registry.
/// </para>
/// </remarks>
public sealed class AwaitingApprovalTileProducer(
    IReadOnlyCnasDbContext db,
    ICnasTimeProvider clock) : IDashboardTileProducer
{
    /// <summary>Roles permitted to approve sensitive admin actions.</summary>
    private static readonly string[] ApproverRoles =
        ["cnas-decider", "cnas-admin", "seful-directiei", "seful-cnas"];

    private readonly IReadOnlyCnasDbContext _db = db;
    // Clock is reserved for "approval backlog older than N days" sub-tiles.
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public DashboardCategory Category => DashboardCategory.ItemsAwaitingApproval;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => ApproverRoles;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        _ = _clock; // reserved — see <remarks>.

        // Maker ≠ checker guard mirrored on the read side so an admin doesn't see
        // their own pending requests in their approval-queue tile.
        var count = await _db.PendingAdminActions
            .Where(p => p.IsActive
                        && p.Status == PendingAdminActionStatus.Pending
                        && p.MakerUserId != userId)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiWidget> widgets =
        [
            new KpiWidget(
                Code: "APPROVAL_QUEUE",
                Title: "Cereri în așteptarea aprobării",
                Value: count,
                Unit: "cereri",
                Category: nameof(DashboardCategory.ItemsAwaitingApproval),
                DeepLinkUrl: "/approvals"),
        ];
        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }
}
