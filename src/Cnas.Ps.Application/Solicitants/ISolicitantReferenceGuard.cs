using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Solicitants;

/// <summary>
/// R0623 / TOR CF 13.04 — pre-flight guard that counts OPEN-state foreign
/// references to a <see cref="Cnas.Ps.Core.Domain.Solicitant"/> row before
/// soft-deletion (deactivation) is allowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate service.</b> The Solicitant table is the anchor of
/// the application → dossier → document → payment → notification value
/// chain. Hard-deleting a Solicitant is forbidden by CLAUDE.md cross-cutting
/// rules (soft-delete only); however, even <i>soft</i> deactivation must not
/// orphan in-flight work — a citizen with an unresolved cerere cannot have
/// their profile silently disabled. The guard centralises the
/// "what is still open against this Solicitant?" question so the deactivation
/// service does not duplicate the scan logic.
/// </para>
/// <para>
/// <b>Open vs. closed.</b> The guard intentionally counts ONLY rows in a
/// non-terminal state:
/// <list type="bullet">
///   <item>Applications: not in (Closed, Approved, Rejected, Withdrawn).</item>
///   <item>Dossiers: <c>ClosedAtUtc IS NULL</c>.</item>
///   <item>Documents: attached to an open dossier (per above).</item>
///   <item>Payments: status in (Scheduled, Issued).</item>
///   <item>Notifications: <c>DeliveryStatus == Pending</c>.</item>
/// </list>
/// Closed / terminal-state rows are historical artefacts and are intentionally
/// excluded — keeping a Solicitant alive forever just because they once
/// received a delivered notification would defeat the right-to-deactivate
/// contract. The exclusion list is part of the contract; expanding or
/// contracting it requires a coordinated update to the integration tests
/// pinning the guard.
/// </para>
/// <para>
/// <b>Read-only contract.</b> The guard injects only
/// <c>Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext</c> — every
/// count flows through the streaming-replica routed context so the pre-flight
/// scan never adds load to the writable primary. The accompanying
/// implementation lives in
/// <c>Cnas.Ps.Infrastructure.Services.Solicitants.SolicitantReferenceGuard</c>.
/// </para>
/// <para>
/// <b>Result.</b> Callers compare
/// <see cref="SolicitantReferenceScanDto.TotalOpen"/> against zero. A non-zero
/// total must surface as
/// <c>Result.Failure(ErrorCodes.SolicitantReferencedByOpenRecords, …)</c> at
/// the calling service; the guard itself never decides policy — it only
/// counts. The depersonalised per-table breakdown is included in the failure
/// detail so the admin UI can render a precise prompt.
/// </para>
/// </remarks>
public interface ISolicitantReferenceGuard
{
    /// <summary>
    /// Counts every OPEN row across the (Application / Dossier / Document /
    /// Payment / Notification) chain that references the targeted Solicitant.
    /// Closed / terminal-state rows are excluded by design — see the type
    /// remarks.
    /// </summary>
    /// <param name="solicitantSqid">
    /// Sqid-encoded id of the Solicitant to scan. Decoded inside the guard
    /// through the injected <c>ISqidService</c>; an invalid Sqid surfaces as
    /// a <see cref="ErrorCodes.InvalidSqid"/> failure.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// A successful <see cref="SolicitantReferenceScanDto"/> describing the
    /// per-table OPEN counts and a <see cref="SolicitantReferenceScanDto.TotalOpen"/>
    /// sum. <see cref="ErrorCodes.InvalidSqid"/> when the Sqid cannot be
    /// decoded; <see cref="ErrorCodes.NotFound"/> when the decoded id does not
    /// correspond to an active Solicitant row.
    /// </returns>
    Task<Result<SolicitantReferenceScanDto>> ScanAsync(
        string solicitantSqid,
        CancellationToken cancellationToken = default);
}
