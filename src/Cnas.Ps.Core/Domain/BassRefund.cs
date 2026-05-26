namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0814 / TOR BP 1.2-E — refund instruction issued from BASS (Budgetul
/// Asigurărilor Sociale de Stat) to a payer (<see cref="Contributor"/>) when
/// the monthly contribution roll-up
/// (<see cref="MonthlyContributionCalculation.OverpaymentAmount"/>) shows the
/// payer is owed money back. One row per (payer × month) that crosses the
/// refund-request boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Rows land in <see cref="BassRefundStatus.Requested"/> on
/// <c>RequestAsync</c>. An admin signs off via <c>ApproveAsync</c> →
/// <see cref="BassRefundStatus.Approved"/>. The Treasury dispatch then flips
/// the row to <see cref="BassRefundStatus.IssuedToTreasury"/> via
/// <c>IssueToTreasuryAsync</c>. Once the Treasury confirms the funds reached
/// the payer's account the row terminates as
/// <see cref="BassRefundStatus.Confirmed"/>. From <c>Requested</c> or
/// <c>Approved</c> the row may also be administratively cancelled
/// (<see cref="BassRefundStatus.Cancelled"/>) with a rationale; cancelling an
/// already-issued refund is refused because the funds are already in flight.
/// </para>
/// <para>
/// <b>Active-uniqueness invariant.</b> At most one non-Cancelled refund row
/// is permitted per (<see cref="ContributorId"/>, <see cref="RelatedMonth"/>)
/// tuple. The configuration enforces this via a filtered unique index. The
/// service layer also pre-checks defensively before insert so the call
/// returns a stable <c>ACTIVE_REFUND_EXISTS</c> message rather than a raw
/// <c>DbUpdateException</c>.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/>
/// because the outbound DTO
/// (<c>Cnas.Ps.Contracts.BassRefundDto.Id</c>) carries a Sqid-encoded
/// surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>Deferred work.</b> The real Treasury dispatch integration is not part
/// of this iteration — <c>IssueToTreasuryAsync</c> only records the operator-
/// supplied dispatch reference. The Treasury-confirmation callback is
/// likewise placeholder-driven via <c>ConfirmAsync</c>.
/// </para>
/// </remarks>
public sealed class BassRefund : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the paying <see cref="Contributor"/> who is
    /// being refunded.
    /// </summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Reporting month the overpayment relates to. By convention the day
    /// component is always 1 — validators enforce <c>Day == 1</c> before
    /// persistence.
    /// </summary>
    public DateOnly RelatedMonth { get; set; }

    /// <summary>
    /// Amount to be refunded (MDL). Strictly positive — the validator bounds
    /// the value to (0, 100_000_000].
    /// </summary>
    public decimal RefundAmount { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to
    /// <see cref="BassRefundStatus.Requested"/>. The service mutates this
    /// field on each lifecycle transition.
    /// </summary>
    public BassRefundStatus Status { get; set; } = BassRefundStatus.Requested;

    /// <summary>
    /// Optional reference to the authorisation document (decision number,
    /// signed PDF, court order, etc.) backing the refund request. Free-form
    /// text up to 256 chars.
    /// </summary>
    public string? AuthorisationDocumentReference { get; set; }

    /// <summary>
    /// Foreign-key reference to the CNAS user (<see cref="UserProfile"/>)
    /// who created the refund request. Always populated.
    /// </summary>
    public long RequestedByUserId { get; set; }

    /// <summary>
    /// Foreign-key reference to the CNAS user who approved the refund. Null
    /// while the row is still in <see cref="BassRefundStatus.Requested"/>.
    /// </summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>
    /// Date the refund request was approved. Null while
    /// <see cref="ApprovedByUserId"/> is null.
    /// </summary>
    public DateOnly? ApprovedDate { get; set; }

    /// <summary>
    /// Treasury-side dispatch reference number recorded by the operator at
    /// <c>IssueToTreasuryAsync</c> time (1..64 chars when set). Null while
    /// the row is still in <see cref="BassRefundStatus.Approved"/> or
    /// earlier.
    /// </summary>
    public string? TreasuryDispatchReference { get; set; }

    /// <summary>
    /// Date the dispatch instruction was sent to the Treasury. Null while
    /// <see cref="TreasuryDispatchReference"/> is null.
    /// </summary>
    public DateOnly? IssuedDate { get; set; }

    /// <summary>
    /// Date the Treasury confirmed the funds reached the payer's account.
    /// Null while the row is not yet in
    /// <see cref="BassRefundStatus.Confirmed"/>.
    /// </summary>
    public DateOnly? ConfirmedDate { get; set; }

    /// <summary>
    /// Operator-supplied rationale for the cancellation (3..500 chars when
    /// set). Null while the row is not cancelled.
    /// </summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// Date the refund request was cancelled. Null while the row is not
    /// cancelled.
    /// </summary>
    public DateOnly? CancelledDate { get; set; }
}
