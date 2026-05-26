using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Solicitants;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Solicitants;

/// <summary>
/// R0623 / TOR CF 13.04 — pure-read implementation of
/// <see cref="ISolicitantReferenceGuard"/>. Counts OPEN-state foreign
/// references to a <see cref="Solicitant"/> by running one count query per
/// referencing table against the read-replica.
/// </summary>
/// <remarks>
/// <para>
/// <b>Open-row predicates.</b> The mapping is hard-coded inside this class
/// so the open/closed contract lives in exactly one place:
/// <list type="bullet">
///   <item>Applications: <c>Status NOT IN (Closed, Approved, Rejected, Withdrawn) AND IsActive</c>.</item>
///   <item>Dossiers: <c>ClosedAtUtc IS NULL AND IsActive</c>, joined to applications owned by the Solicitant.</item>
///   <item>Documents: belong to an open dossier (per above) AND <c>IsActive</c>.</item>
///   <item>Payments: <c>Status IN (Scheduled, Issued) AND IsActive</c>, joined via <c>BeneficiarySolicitantId</c>.</item>
///   <item>Notifications: <c>DeliveryStatus == Pending AND IsActive</c>, addressed to the Solicitant's user id.</item>
/// </list>
/// Closed / terminal-state rows are intentionally excluded — a Solicitant
/// with only historical artefacts may be deactivated.
/// </para>
/// <para>
/// <b>Read-only contract.</b> The guard injects only
/// <see cref="IReadOnlyCnasDbContext"/>; every count flows through the
/// streaming-replica routed context. The pre-flight scan therefore never
/// adds load to the writable primary.
/// </para>
/// <para>
/// <b>Sensitivity.</b> The guard returns only per-table OPEN-row counts;
/// no row content ever leaves the service. Safe to call from any
/// admin-authenticated path.
/// </para>
/// </remarks>
public sealed class SolicitantReferenceGuard : ISolicitantReferenceGuard
{
    /// <summary>Read-replica routed DbContext (per-request scope).</summary>
    private readonly IReadOnlyCnasDbContext _readDb;

    /// <summary>Sqid encoder/decoder used at the boundary (CLAUDE.md RULE 3).</summary>
    private readonly ISqidService _sqids;

    /// <summary>
    /// Constructs the guard with its two dependencies.
    /// </summary>
    /// <param name="readDb">Read-replica routed DbContext (per-request scope).</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    public SolicitantReferenceGuard(IReadOnlyCnasDbContext readDb, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(sqids);
        _readDb = readDb;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<SolicitantReferenceScanDto>> ScanAsync(
        string solicitantSqid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitantSqid);

        // Sqid decode at the boundary — internal counts run against the raw long.
        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return Result<SolicitantReferenceScanDto>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var solicitantId = decoded.Value;

        // The Solicitant row must exist AND be active — soft-deleting an already-
        // deactivated row would be a no-op idempotent path (handled at the service
        // layer); requesting a scan against a non-existent id is a NotFound.
        var exists = await _readDb.Solicitants
            .Where(s => s.Id == solicitantId && s.IsActive)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return Result<SolicitantReferenceScanDto>.Failure(
                ErrorCodes.NotFound,
                $"Solicitant '{solicitantSqid}' was not found or is already inactive.");
        }

        // ── Per-table OPEN counts. Each query is independent; sequential because
        //    the read context is not designed for DbContext-per-query parallelism.

        // Applications: open == NOT in terminal status set AND IsActive.
        var applicationsOpen = await _readDb.Applications
            .Where(a => a.SolicitantId == solicitantId
                        && a.IsActive
                        && a.Status != ApplicationStatus.Closed
                        && a.Status != ApplicationStatus.Approved
                        && a.Status != ApplicationStatus.Rejected
                        && a.Status != ApplicationStatus.Withdrawn)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Dossiers: ClosedAtUtc IS NULL AND IsActive, joined to applications owned
        // by the Solicitant. We re-select via the navigation FK to avoid a literal
        // SQL JOIN that the InMemory provider does not always honour identically
        // to the relational provider — a sub-query against Applications matches
        // both providers' translation cleanly.
        var dossiersOpen = await _readDb.Dossiers
            .Where(d => d.IsActive
                        && d.ClosedAtUtc == null
                        && _readDb.Applications.Any(a =>
                            a.Id == d.ApplicationId && a.SolicitantId == solicitantId))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Documents: attached to an open dossier owned by the Solicitant.
        var documentsOpen = await _readDb.Documents
            .Where(doc => doc.IsActive
                          && doc.DossierId != null
                          && _readDb.Dossiers.Any(d =>
                              d.Id == doc.DossierId
                              && d.IsActive
                              && d.ClosedAtUtc == null
                              && _readDb.Applications.Any(a =>
                                  a.Id == d.ApplicationId && a.SolicitantId == solicitantId)))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Payments: BenefitPayment in (Scheduled, Issued) for this beneficiary.
        var paymentsOpen = await _readDb.BenefitPayments
            .Where(p => p.IsActive
                        && p.BeneficiarySolicitantId == solicitantId
                        && (p.Status == BenefitPaymentStatus.Scheduled
                            || p.Status == BenefitPaymentStatus.Issued))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Notifications: still Pending delivery. The Notification row carries
        // RecipientUserId (not SolicitantId); in the citizen-facing flow the
        // applicant's UserProfile row shares the same surrogate id as their
        // Solicitant row (the seeding path creates both with the same Id) —
        // counting on RecipientUserId == solicitantId mirrors how
        // NotificationService.EnqueueAsync addresses citizen notifications.
        var notificationsOpen = await _readDb.Notifications
            .Where(n => n.IsActive
                        && n.RecipientUserId == solicitantId
                        && n.DeliveryStatus == NotificationDeliveryStatus.Pending)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        var total = applicationsOpen + dossiersOpen + documentsOpen + paymentsOpen + notificationsOpen;

        return Result<SolicitantReferenceScanDto>.Success(new SolicitantReferenceScanDto(
            SolicitantSqid: solicitantSqid,
            ApplicationsOpen: applicationsOpen,
            DossiersOpen: dossiersOpen,
            DocumentsOpen: documentsOpen,
            PaymentsOpen: paymentsOpen,
            NotificationsOpen: notificationsOpen,
            TotalOpen: total));
    }
}
