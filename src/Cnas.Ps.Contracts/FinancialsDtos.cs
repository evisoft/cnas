using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0814 / R0815 — BASS refunds + Treasury payment corrections (BP 1.2-E / 1.2-F)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0814 / BP 1.2-E — one BASS-to-payer refund instruction as it leaves the
/// system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying refund row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the paying contributor being refunded.</param>
/// <param name="RelatedMonth">Reporting month the overpayment relates to (day = 1).</param>
/// <param name="RefundAmount">Amount to be refunded (MDL).</param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.BassRefundStatus</c> value (<c>Requested</c>,
/// <c>Approved</c>, <c>IssuedToTreasury</c>, <c>Confirmed</c>,
/// <c>Cancelled</c>).
/// </param>
/// <param name="AuthorisationDocumentReference">Optional reference to the supporting authorisation document.</param>
/// <param name="RequestedByUserSqid">Sqid-encoded id of the user who requested the refund.</param>
/// <param name="ApprovedByUserSqid">Sqid-encoded id of the user who approved the refund, when applicable.</param>
/// <param name="ApprovedDate">Date the refund was approved, when applicable.</param>
/// <param name="TreasuryDispatchReference">Treasury dispatch reference, when issued.</param>
/// <param name="IssuedDate">Date the dispatch instruction was sent to the Treasury, when applicable.</param>
/// <param name="ConfirmedDate">Date the Treasury confirmed the refund landed, when applicable.</param>
/// <param name="CancelReason">Operator-supplied rationale when cancelled.</param>
/// <param name="CancelledDate">Date the refund was cancelled, when applicable.</param>
public sealed record BassRefundDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly RelatedMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RefundAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AuthorisationDocumentReference,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RequestedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ApprovedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ApprovedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TreasuryDispatchReference,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? IssuedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ConfirmedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? CancelledDate);

/// <summary>
/// R0814 / BP 1.2-E — input DTO for
/// <c>POST /api/bass-refunds/request</c>. Identifies the (payer, month)
/// tuple to refund and carries the refund amount + optional supporting
/// reference.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="RelatedMonth">Reporting month with the overpayment (day = 1).</param>
/// <param name="RefundAmount">Refund amount (MDL, &gt; 0, ≤ 100_000_000).</param>
/// <param name="AuthorisationDocumentReference">Optional document reference (0..256 chars).</param>
public sealed record BassRefundRequestInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly RelatedMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RefundAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AuthorisationDocumentReference = null);

/// <summary>
/// R0814 / BP 1.2-E — input DTO for
/// <c>POST /api/bass-refunds/{sqid}/issue-to-treasury</c>. Carries the
/// operator-supplied Treasury dispatch reference number.
/// </summary>
/// <param name="TreasuryDispatchReference">Treasury dispatch reference (1..64 chars).</param>
public sealed record BassRefundIssueInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TreasuryDispatchReference);

/// <summary>
/// R0814 / BP 1.2-E — input DTO for
/// <c>POST /api/bass-refunds/{sqid}/confirm</c>. Carries the date the
/// Treasury confirmed the refund landed in the payer's account.
/// </summary>
/// <param name="ConfirmedDate">Date of confirmation; must be ≤ today.</param>
public sealed record BassRefundConfirmInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ConfirmedDate);

/// <summary>
/// R0814 / BP 1.2-E — input DTO for
/// <c>POST /api/bass-refunds/{sqid}/cancel</c>. Carries the operator-supplied
/// rationale only.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record BassRefundCancelInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R0815 / BP 1.2-F — one Treasury-payment correction row as it leaves the
/// system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the correction row.</param>
/// <param name="OriginalReceiptSqid">Sqid-encoded id of the receipt being corrected.</param>
/// <param name="Kind">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.PaymentCorrectionKind</c> value (<c>Reverse</c>,
/// <c>RedirectToPayer</c>, <c>RedirectToMonth</c>, <c>AdjustAmount</c>).
/// </param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.PaymentCorrectionStatus</c> value (<c>Draft</c>,
/// <c>Approved</c>, <c>Applied</c>, <c>Cancelled</c>).
/// </param>
/// <param name="RedirectedToContributorSqid">Sqid-encoded id of the target payer for redirect-to-payer corrections.</param>
/// <param name="RedirectedToMonth">Target reporting month for redirect-to-month corrections.</param>
/// <param name="AdjustedAmount">Adjusted amount for amount-adjustment corrections.</param>
/// <param name="RequestedByUserSqid">Sqid-encoded id of the user who drafted the correction.</param>
/// <param name="ApprovedByUserSqid">Sqid-encoded id of the user who approved the correction, when applicable.</param>
/// <param name="Reason">Operator-supplied rationale (mandatory).</param>
/// <param name="CreatedUtc">UTC instant the row was created.</param>
/// <param name="AppliedUtc">UTC instant the correction was applied to the underlying receipt.</param>
/// <param name="CancelReason">Operator-supplied rationale when cancelled.</param>
public sealed record PaymentCorrectionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string OriginalReceiptSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RedirectedToContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? RedirectedToMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? AdjustedAmount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RequestedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ApprovedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? AppliedUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason);

/// <summary>
/// R0815 / BP 1.2-F — input DTO for
/// <c>POST /api/payment-corrections</c>. Captures the target receipt, kind,
/// and the per-kind redirect target / adjusted amount as appropriate.
/// </summary>
/// <param name="OriginalReceiptSqid">Sqid-encoded id of the receipt being corrected.</param>
/// <param name="Kind">Stable <c>PaymentCorrectionKind</c> enum name.</param>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
/// <param name="RedirectedToContributorSqid">Sqid-encoded id of the target payer; required when Kind=RedirectToPayer.</param>
/// <param name="RedirectedToMonth">Target month (day = 1); required when Kind=RedirectToMonth.</param>
/// <param name="AdjustedAmount">Adjusted amount; required when Kind=AdjustAmount.</param>
public sealed record PaymentCorrectionCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string OriginalReceiptSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RedirectedToContributorSqid = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? RedirectedToMonth = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? AdjustedAmount = null);

/// <summary>
/// R0815 / BP 1.2-F — input DTO for
/// <c>POST /api/payment-corrections/{sqid}/cancel</c>. Carries the
/// operator-supplied rationale only.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record PaymentCorrectionCancelInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

// ────────────────────────────────────────────────────────────────────────────
// R0816 — Treasury information export (BP 1.2-G)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0816 / BP 1.2-G — machine-readable export of refund instructions and
/// outstanding-claim expectations dispatched to the State Treasury.
/// </summary>
/// <param name="Format">Stable format tag — <c>XML</c> or <c>CSV</c>.</param>
/// <param name="FileName">Suggested filename (e.g. <c>treasury-info-2026-05-22.xml</c>).</param>
/// <param name="Content">Encoded payload bytes — UTF-8 for both XML and CSV.</param>
/// <param name="RefundCount">Number of refund instructions emitted in the payload.</param>
/// <param name="OutstandingClaimCount">Number of outstanding claim expectations emitted.</param>
/// <param name="TotalRefundAmount">Sum of every refund amount (MDL).</param>
/// <param name="TotalOutstandingAmount">Sum of every outstanding-claim remaining amount (MDL).</param>
public sealed record TreasuryInformationExportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Format,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string FileName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    byte[] Content,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int RefundCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int OutstandingClaimCount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalRefundAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalOutstandingAmount);

// ────────────────────────────────────────────────────────────────────────────
// R0817 — Staggered penalty repayment (BP 1.2-H)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0817 / BP 1.2-H — one staggered-repayment plan row as it leaves the
/// system. Children (installments) live in
/// <see cref="PenaltyRepaymentInstallmentDto"/>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the plan row.</param>
/// <param name="LatePaymentPenaltySqid">Sqid-encoded id of the parent late-payment penalty row.</param>
/// <param name="InstallmentCount">Number of installments the penalty was split into (2..36).</param>
/// <param name="InstallmentAmount">Per-installment nominal amount (MDL); the final installment may absorb rounding.</param>
/// <param name="FirstInstallmentDueDate">Statutory due date of the first installment.</param>
/// <param name="Status">Stable enum-name representation of <c>PenaltyRepaymentPlanStatus</c>.</param>
/// <param name="PaidInstallmentCount">Running count of paid installments.</param>
/// <param name="RemainingAmount">Cached remainder owed across all unpaid installment rows (MDL).</param>
/// <param name="CreatedUtc">UTC instant the plan was created.</param>
/// <param name="CompletedUtc">UTC instant the plan was completed, when applicable.</param>
/// <param name="CancelledUtc">UTC instant the plan was cancelled, when applicable.</param>
/// <param name="CancelReason">Operator-supplied rationale when cancelled.</param>
public sealed record PenaltyRepaymentPlanDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string LatePaymentPenaltySqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int InstallmentCount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal InstallmentAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly FirstInstallmentDueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PaidInstallmentCount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RemainingAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CancelledUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason);

/// <summary>
/// R0817 / BP 1.2-H — one installment row attached to a parent
/// <see cref="PenaltyRepaymentPlanDto"/>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the installment row.</param>
/// <param name="PenaltyRepaymentPlanSqid">Sqid-encoded id of the parent plan row.</param>
/// <param name="InstallmentNumber">1-based ordinal position within the plan.</param>
/// <param name="DueDate">Statutory due date for this installment.</param>
/// <param name="Amount">Nominal amount owed (MDL).</param>
/// <param name="PaidDate">Date the installment was paid, when applicable.</param>
/// <param name="PaidAmount">Actual amount received (MDL), when applicable.</param>
/// <param name="IsPaid">Settlement flag.</param>
public sealed record PenaltyRepaymentInstallmentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PenaltyRepaymentPlanSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int InstallmentNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly DueDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? PaidAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsPaid);

/// <summary>
/// R0817 / BP 1.2-H — input DTO for <c>POST /api/penalty-repayment-plans</c>.
/// Identifies the late-payment penalty + plan shape.
/// </summary>
/// <param name="LatePaymentPenaltySqid">Sqid-encoded id of the parent late-payment-penalty row.</param>
/// <param name="InstallmentCount">Number of installments (inclusive 2..36).</param>
/// <param name="FirstInstallmentDueDate">Statutory due date of the first installment; must be ≥ today.</param>
public sealed record PenaltyRepaymentCreatePlanInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string LatePaymentPenaltySqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int InstallmentCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly FirstInstallmentDueDate);

/// <summary>
/// R0817 / BP 1.2-H — input DTO for
/// <c>POST /api/penalty-repayment-plans/{sqid}/installments/{installmentNumber}/pay</c>.
/// Carries the actual payment-date and -amount captured by the operator.
/// </summary>
/// <param name="PaidDate">Date the installment was settled; must be ≤ today.</param>
/// <param name="PaidAmount">Actual amount received (MDL).</param>
public sealed record PenaltyRepaymentRegisterPaymentInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal PaidAmount);

/// <summary>
/// R0817 / BP 1.2-H — input DTO for
/// <c>POST /api/penalty-repayment-plans/{sqid}/cancel</c>. Carries the
/// operator-supplied rationale only.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record PenaltyRepaymentCancelPlanInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);
