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
/// R0533 / TOR CF 04.04 — KPI grid cell counting active maker-checker actions
/// awaiting decider input that the caller did NOT originate. Models the "Docs
/// pending approval - 41" KPI on the dashboard (counts the rows the operator MUST
/// triage, not the rows the operator created).
/// </summary>
/// <remarks>
/// <para>
/// The cell uses <see cref="PendingAdminAction"/> rows (R0058) as the canonical
/// pending-approval substrate — the same substrate
/// <see cref="AwaitingApprovalTileProducer"/> reads. The maker-checker invariant
/// is mirrored on the read side: a maker cannot count their own pending request as
/// "to triage".
/// </para>
/// <para>
/// <b>Role gating.</b> Deciders / admins / direction-heads / cnas-heads only —
/// matches the upstream tile's allow-list so an operator cannot see an empty
/// approval-queue KPI when they have no business approving anything.
/// </para>
/// </remarks>
public sealed class DocsPendingApprovalKpiGridProducer(
    IReadOnlyCnasDbContext db) : IKpiGridProducer
{
    /// <summary>Stable KPI cell code.</summary>
    public const string CellCode = "DOCS_PENDING_APPROVAL";

    /// <summary>Approver role allow-list (mirrors AwaitingApprovalTileProducer).</summary>
    private static readonly string[] ApproverRoles =
        ["cnas-decider", "cnas-admin", "seful-directiei", "seful-cnas"];

    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => ApproverRoles;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiGridCellDto>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var count = await _db.PendingAdminActions
            .Where(p => p.IsActive
                        && p.Status == PendingAdminActionStatus.Pending
                        && p.MakerUserId != userId)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiGridCellDto> cells =
        [
            new KpiGridCellDto(
                Code: CellCode,
                Title: "Documente în așteptarea aprobării",
                Value: count,
                Trend: null,
                DeepLinkUrl: "/approvals"),
        ];
        return Result<IReadOnlyList<KpiGridCellDto>>.Success(cells);
    }
}
