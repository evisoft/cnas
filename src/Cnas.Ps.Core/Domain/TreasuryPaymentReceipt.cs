namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0911 / TOR BP 2.2-B — single Treasury payment-receipt row imported from
/// the daily Treasury feed. The reconciliation pipeline matches each receipt
/// against the expected <see cref="Rev5DeclarationRow"/> contributions for the
/// payer's (<see cref="PayerContributorId"/>, <see cref="ReportingMonth"/>)
/// tuple and proportionally credits each insured person's
/// <see cref="PersonalAccountEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Receipts land in
/// <see cref="TreasuryPaymentDistributionStatus.Pending"/> after
/// <c>ImportReceiptAsync</c>; the <c>TreasuryDistributionJob</c> background
/// job picks them up every 15 minutes and flips them to a terminal state
/// (<see cref="TreasuryPaymentDistributionStatus.Distributed"/>,
/// <see cref="TreasuryPaymentDistributionStatus.PartiallyDistributed"/>, or
/// <see cref="TreasuryPaymentDistributionStatus.Failed"/>).
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b>
/// <see cref="TreasuryReferenceNumber"/> is the Treasury-side primary key —
/// re-importing the same reference twice is a no-op (the service returns the
/// stable <c>DUPLICATE_TREASURY_REFERENCE</c> failure). Enforced by a unique
/// index in <c>TreasuryPaymentReceiptConfiguration</c>.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/>
/// because operators reference an individual receipt when triggering manual
/// re-distribution. The outbound DTO surfaces a Sqid-encoded surrogate per
/// CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class TreasuryPaymentReceipt : AuditableEntity, IExternalId
{
    /// <summary>
    /// External reference number assigned by the Treasury's payment system.
    /// Required; 1..64 chars enforced by the validator. Participates in the
    /// natural-key uniqueness rule.
    /// </summary>
    public required string TreasuryReferenceNumber { get; set; }

    /// <summary>
    /// Date the Treasury processed the payment (as reported by the feed).
    /// Distinct from <see cref="AuditableEntity.CreatedAtUtc"/> which captures
    /// the row-creation instant inside CNAS.
    /// </summary>
    public DateOnly ReceiptDate { get; set; }

    /// <summary>
    /// Foreign-key reference to the paying <see cref="Contributor"/>
    /// (employer / individual paying social-insurance contributions). Raw
    /// bigint id — only the outbound DTO surfaces a Sqid-encoded form per
    /// CLAUDE.md RULE 3.
    /// </summary>
    public long PayerContributorId { get; set; }

    /// <summary>
    /// Calendar month the payment is attributed to. By convention the day
    /// component is always 1 — validators enforce <c>Day == 1</c> before
    /// persistence.
    /// </summary>
    public DateOnly ReportingMonth { get; set; }

    /// <summary>
    /// Amount received (MDL). Strictly positive — the validator bounds the
    /// value to (0, 100_000_000].
    /// </summary>
    public decimal AmountReceived { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to
    /// <see cref="TreasuryPaymentDistributionStatus.Pending"/>. The
    /// distribution job mutates this field on each fire.
    /// </summary>
    public TreasuryPaymentDistributionStatus DistributionStatus { get; set; }
        = TreasuryPaymentDistributionStatus.Pending;

    /// <summary>
    /// UTC instant the distribution job completed for this receipt. Null while
    /// pending; populated when the job transitions the row to a terminal
    /// state.
    /// </summary>
    public DateTime? DistributedAtUtc { get; set; }

    /// <summary>
    /// Stable failure-reason string populated when
    /// <see cref="DistributionStatus"/> is
    /// <see cref="TreasuryPaymentDistributionStatus.Failed"/>. Typical value:
    /// <c>"NO_REV5_TO_DISTRIBUTE"</c>. Null otherwise.
    /// </summary>
    public string? DistributionFailureReason { get; set; }

    /// <summary>
    /// Amount that could not be distributed (MDL). Equals
    /// <see cref="AmountReceived"/> when status is
    /// <see cref="TreasuryPaymentDistributionStatus.Failed"/>; positive but
    /// smaller when status is
    /// <see cref="TreasuryPaymentDistributionStatus.PartiallyDistributed"/>;
    /// zero or null when status is
    /// <see cref="TreasuryPaymentDistributionStatus.Distributed"/>.
    /// </summary>
    public decimal? UndistributedRemainderAmount { get; set; }
}
