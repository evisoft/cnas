using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0831 / R0832 — Claims (creanțe) registry + claim payments (BP 1.3-B / 1.3-C)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0831 / BP 1.3-B — one claim (creanță) row as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying claim row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the owning payer (Contributor).</param>
/// <param name="ClaimNumber">External stable identifier in the format <c>CRN-{year}-{seq:000000}</c>.</param>
/// <param name="Kind">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.ClaimKind</c> value (<c>Contribution</c>,
/// <c>LatePenalty</c>, <c>AdminFine</c>, <c>Court</c>, <c>Other</c>).
/// </param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.ClaimStatus</c> value (<c>Open</c>,
/// <c>PartiallyPaid</c>, <c>Settled</c>, <c>Cancelled</c>, <c>Disputed</c>).
/// </param>
/// <param name="PrincipalAmount">Original outstanding amount owed (MDL).</param>
/// <param name="PaidAmount">Running total of payments received (MDL).</param>
/// <param name="RemainingAmount">Computed remainder owed (MDL) — <c>Principal − Paid</c>.</param>
/// <param name="RelatedMonth">Reporting month the claim relates to (day = 1).</param>
/// <param name="OpenedDate">Date the claim was opened.</param>
/// <param name="DueDate">Optional administrative deadline by which the claim should be settled.</param>
/// <param name="SettledDate">Date the claim was fully settled, when applicable.</param>
/// <param name="CancelledDate">Date the claim was cancelled, when applicable.</param>
/// <param name="CancelReason">Operator-supplied rationale when cancelled.</param>
/// <param name="RelatedDocumentReference">Cross-reference to the source document (court order, audit report, ...), when set.</param>
public sealed record ClaimDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ClaimNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PrincipalAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PaidAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RemainingAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly RelatedMonth,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly OpenedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? DueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? SettledDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? CancelledDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RelatedDocumentReference);

/// <summary>
/// R0831 / BP 1.3-B — input DTO for the
/// <c>POST /api/claims</c> endpoint. <c>ClaimNumber</c> is NOT a client input —
/// it is generated server-side.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="Kind">Stable <c>ClaimKind</c> enum name.</param>
/// <param name="RelatedMonth">Reporting month the claim relates to (day = 1).</param>
/// <param name="PrincipalAmount">Outstanding amount owed (MDL, &gt; 0, ≤ 100_000_000).</param>
/// <param name="OpenedDate">Date the claim was opened (defaults to today when null).</param>
/// <param name="DueDate">Optional administrative deadline; must be ≥ <paramref name="OpenedDate"/> when supplied.</param>
/// <param name="RelatedDocumentReference">Optional cross-reference to the source document (≤ 256 chars).</param>
public sealed record ClaimRegisterInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly RelatedMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PrincipalAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly OpenedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? DueDate = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RelatedDocumentReference = null);

/// <summary>
/// R0831 / BP 1.3-B — input DTO for the
/// <c>PUT /api/claims/{sqid}</c> endpoint. Modifies the principal amount, due
/// date and related-document reference of an outstanding claim. Rejected when
/// the claim has already been settled or cancelled.
/// </summary>
/// <param name="PrincipalAmount">New principal amount (MDL, &gt; 0, ≤ 100_000_000); null leaves unchanged.</param>
/// <param name="DueDate">New due date; null leaves unchanged.</param>
/// <param name="RelatedDocumentReference">New cross-reference; null leaves unchanged.</param>
/// <param name="ChangeReason">Operator-supplied rationale for the change (3..500 chars).</param>
public sealed record ClaimModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? PrincipalAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? DueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RelatedDocumentReference,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R0832 / BP 1.3-C — one claim-payment row as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying payment row.</param>
/// <param name="ClaimSqid">Sqid-encoded id of the parent claim.</param>
/// <param name="PaidDate">Calendar date the payment was received.</param>
/// <param name="Amount">Payment amount (MDL).</param>
/// <param name="PaymentReference">Optional external payment reference, when supplied.</param>
/// <param name="TreasuryReceiptSqid">Sqid-encoded id of the underlying Treasury receipt, when linked.</param>
/// <param name="Notes">Operator notes attached to the payment row, when set.</param>
public sealed record ClaimPaymentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ClaimSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? PaymentReference,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? TreasuryReceiptSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R0832 / BP 1.3-C — input DTO for the
/// <c>POST /api/claims/{sqid}/payments</c> endpoint. Adds a payment row
/// against an existing claim.
/// </summary>
/// <param name="PaidDate">Calendar date the payment was received; must not be in the future.</param>
/// <param name="Amount">Payment amount (MDL, &gt; 0, ≤ 100_000_000).</param>
/// <param name="PaymentReference">Optional external payment reference (≤ 64 chars).</param>
/// <param name="TreasuryReceiptSqid">Optional Sqid-encoded id of the underlying Treasury receipt.</param>
/// <param name="Notes">Optional operator notes (0..1000 chars).</param>
public sealed record ClaimPaymentInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? PaymentReference = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? TreasuryReceiptSqid = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes = null);

/// <summary>
/// R0831 / BP 1.3-B — input DTO for the cancel / dispute endpoints. Carries
/// the operator-supplied rationale only.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record ClaimReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);
