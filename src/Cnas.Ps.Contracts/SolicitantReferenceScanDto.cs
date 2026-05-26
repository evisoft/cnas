using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0623 / TOR CF 13.04 — Solicitant-row reference-blocking DTO. Surfaced by
// the ISolicitantReferenceGuard pre-flight scan that runs before a Solicitant
// deactivation (soft-delete) is allowed. The shape is depersonalised — only
// per-table OPEN-row counts cross the boundary, never the referencing rows
// themselves.
//
// Per CLAUDE.md RULE 3 / TOR CF 13.04 the guard distinguishes OPEN rows
// (active applications, dossiers, undelivered notifications, pending
// payments, current documents) from CLOSED / terminal-state rows. The
// deactivation block fires ONLY when any of the OPEN counters is non-zero;
// terminal-state references (closed applications, delivered notifications,
// paid / cancelled / returned payments, ...) do NOT block the soft-delete.
//
// Sensitivity: Internal — operator-facing only. Contracts MUST NOT carry
// <see cref="…"/> into Cnas.Ps.Core per project layering rules.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0623 / TOR CF 13.04 — depersonalised per-table breakdown of how many
/// <i>open</i> rows currently reference the targeted <c>Solicitant</c>.
/// Returned by <c>ISolicitantReferenceGuard.ScanAsync</c> and consumed by
/// the admin "deactivate Solicitant" UI as a preview before attempting the
/// soft-delete. Every counter is a non-negative <see cref="long"/>;
/// <see cref="TotalOpen"/> is the sum across every per-table counter and
/// drives the block-or-allow decision.
/// </summary>
/// <param name="SolicitantSqid">
/// Sqid-encoded id of the Solicitant the scan ran against. Echoed back so
/// the caller can match async results to the originating request without
/// re-decoding.
/// </param>
/// <param name="ApplicationsOpen">
/// Count of <c>ServiceApplication</c> rows that are NOT in a terminal state
/// (Closed / Approved / Rejected / Withdrawn) and still cite this
/// Solicitant via <c>ServiceApplication.SolicitantId</c>.
/// </param>
/// <param name="DossiersOpen">
/// Count of <c>Dossier</c> rows that have not yet been closed
/// (<c>Dossier.ClosedAtUtc IS NULL</c>) and belong to an application owned
/// by this Solicitant.
/// </param>
/// <param name="DocumentsOpen">
/// Count of <c>Document</c> rows that are attached to an open dossier owned
/// by this Solicitant. Documents on closed dossiers do not block — they are
/// historical artefacts.
/// </param>
/// <param name="PaymentsOpen">
/// Count of <c>BenefitPayment</c> rows in a non-terminal state
/// (<c>Scheduled</c> / <c>Issued</c>) for which this Solicitant is the
/// beneficiary. <c>Paid</c> / <c>Returned</c> / <c>Cancelled</c> rows do
/// not block — they are terminal ledger entries.
/// </param>
/// <param name="NotificationsOpen">
/// Count of <c>Notification</c> rows still pending delivery
/// (<c>DeliveryStatus == Pending</c>) addressed to a user who shares this
/// Solicitant's internal id. Delivered / Failed / Suppressed notifications
/// do not block — they are terminal ledger entries.
/// </param>
/// <param name="TotalOpen">
/// Sum across every per-table counter above. Zero means "soft-delete is
/// permitted"; any non-zero total surfaces as a
/// <c>SOLICITANT.REFERENCED_BY_OPEN_RECORDS</c> failure when the caller
/// attempts to deactivate.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SolicitantReferenceScanDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SolicitantSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long ApplicationsOpen,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long DossiersOpen,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long DocumentsOpen,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long PaymentsOpen,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long NotificationsOpen,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalOpen);
