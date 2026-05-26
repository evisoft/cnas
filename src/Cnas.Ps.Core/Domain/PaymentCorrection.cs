namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0815 / TOR BP 1.2-F — correction applied to an underlying
/// <see cref="TreasuryPaymentReceipt"/> when a payment was mis-routed (wrong
/// payer, wrong month) or over-paid. One row per corrective action; the
/// service layer applies the actual mutation only when the row transitions
/// to <see cref="PaymentCorrectionStatus.Applied"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Rows land in <see cref="PaymentCorrectionStatus.Draft"/>
/// on <c>CreateAsync</c>. An admin signs off via <c>ApproveAsync</c> →
/// <see cref="PaymentCorrectionStatus.Approved"/>. The
/// <c>ApplyAsync</c> path then performs the actual receipt mutation in the
/// same transaction as the status flip to
/// <see cref="PaymentCorrectionStatus.Applied"/>. From <c>Draft</c> the row
/// may also flip to <see cref="PaymentCorrectionStatus.Cancelled"/> with a
/// rationale; cancelling an already-approved correction is refused because
/// the operator would have to re-draft it anyway.
/// </para>
/// <para>
/// <b>Mutation matrix.</b> The applied mutation depends on
/// <see cref="Kind"/>:
/// <list type="bullet">
///   <item><see cref="PaymentCorrectionKind.Reverse"/> — receipt
///     <c>DistributionStatus</c> ← <c>Failed</c>,
///     <c>UndistributedRemainderAmount</c> ← <c>AmountReceived</c>.</item>
///   <item><see cref="PaymentCorrectionKind.RedirectToPayer"/> — receipt
///     <c>PayerContributorId</c> ← <see cref="RedirectedToContributorId"/>.</item>
///   <item><see cref="PaymentCorrectionKind.RedirectToMonth"/> — receipt
///     <c>ReportingMonth</c> ← <see cref="RedirectedToMonth"/>.</item>
///   <item><see cref="PaymentCorrectionKind.AdjustAmount"/> — receipt
///     <c>AmountReceived</c> ← <see cref="AdjustedAmount"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Audit chain.</b> Both the original
/// <see cref="OriginalTreasuryPaymentReceiptId"/> and (when applicable) the
/// redirected target id are captured on the row so an investigator can
/// reconstruct the mutation history without joining against the audit
/// journal.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/>
/// because the outbound DTO carries a Sqid-encoded surrogate per CLAUDE.md
/// RULE 3.
/// </para>
/// </remarks>
public sealed class PaymentCorrection : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the <see cref="TreasuryPaymentReceipt"/>
    /// being corrected. Required.
    /// </summary>
    public long OriginalTreasuryPaymentReceiptId { get; set; }

    /// <summary>
    /// Foreign-key reference to the redirect-target
    /// <see cref="Contributor"/> when
    /// <see cref="Kind"/> is
    /// <see cref="PaymentCorrectionKind.RedirectToPayer"/>. Null for every
    /// other kind.
    /// </summary>
    public long? RedirectedToContributorId { get; set; }

    /// <summary>
    /// Redirect-target reporting month when <see cref="Kind"/> is
    /// <see cref="PaymentCorrectionKind.RedirectToMonth"/>. Day component
    /// must be 1. Null for every other kind.
    /// </summary>
    public DateOnly? RedirectedToMonth { get; set; }

    /// <summary>
    /// Classification of the correction — drives the
    /// <c>ApplyAsync</c> mutation path.
    /// </summary>
    public PaymentCorrectionKind Kind { get; set; }

    /// <summary>
    /// Adjusted <c>AmountReceived</c> override when <see cref="Kind"/> is
    /// <see cref="PaymentCorrectionKind.AdjustAmount"/>. Validator enforces
    /// (0, original.AmountReceived]. Null for every other kind.
    /// </summary>
    public decimal? AdjustedAmount { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to
    /// <see cref="PaymentCorrectionStatus.Draft"/>.
    /// </summary>
    public PaymentCorrectionStatus Status { get; set; } = PaymentCorrectionStatus.Draft;

    /// <summary>
    /// Foreign-key reference to the CNAS user (<see cref="UserProfile"/>)
    /// who drafted the correction. Always populated.
    /// </summary>
    public long RequestedByUserId { get; set; }

    /// <summary>
    /// Foreign-key reference to the CNAS user who approved the correction.
    /// Null while the row is still in
    /// <see cref="PaymentCorrectionStatus.Draft"/>.
    /// </summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>
    /// Mandatory operator-supplied rationale documenting WHY the correction
    /// is needed (3..500 chars). Captured at draft time and immutable
    /// thereafter so the audit trail stays honest.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>
    /// UTC instant the row was created. Mirrored from
    /// <see cref="AuditableEntity.CreatedAtUtc"/> at insert time for symmetry
    /// with <see cref="AppliedUtc"/> — auditors compare the two on the same
    /// row.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// UTC instant the correction was applied to the underlying receipt.
    /// Null while the row is not yet in
    /// <see cref="PaymentCorrectionStatus.Applied"/>.
    /// </summary>
    public DateTime? AppliedUtc { get; set; }

    /// <summary>
    /// Operator-supplied rationale for cancellation (3..500 chars when set).
    /// Null while the row is not cancelled.
    /// </summary>
    public string? CancelReason { get; set; }
}
