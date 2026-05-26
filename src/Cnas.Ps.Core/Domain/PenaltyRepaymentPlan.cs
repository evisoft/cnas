namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0817 / TOR BP 1.2-H — staggered-repayment plan that splits a single
/// <see cref="LatePaymentPenalty"/> into <c>N</c> installments (2..36). At most
/// one Active plan per penalty; once Completed / Defaulted / Cancelled the row
/// is terminal and a brand-new plan must be created if the operator wants to
/// restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Rows land in <see cref="PenaltyRepaymentPlanStatus.Active"/>
/// on <c>CreatePlanAsync</c>. Each <c>RegisterInstallmentPaymentAsync</c> call
/// bumps <see cref="PaidInstallmentCount"/> and updates
/// <see cref="RemainingAmount"/>. When the last installment is paid the row
/// flips to <see cref="PenaltyRepaymentPlanStatus.Completed"/> and stamps
/// <see cref="CompletedUtc"/>. The background-detection job
/// (<c>PenaltyRepaymentDefaultDetectionJob</c>) flips Active rows to
/// <see cref="PenaltyRepaymentPlanStatus.Defaulted"/> when any installment is
/// past due AND not paid for &gt; 30 days. Admin <c>CancelPlanAsync</c>
/// terminates the plan with a rationale captured in
/// <see cref="CancelReason"/>.
/// </para>
/// <para>
/// <b>Installment-amount distribution.</b> The service computes
/// <c>InstallmentAmount = round(PenaltyAmount / InstallmentCount, 2)</c>; the
/// final installment row absorbs any rounding residual so the sum of every
/// row's <c>Amount</c> equals the original penalty exactly (CLAUDE.md
/// "Immutable Snapshots").
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the outbound DTO carries the Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class PenaltyRepaymentPlan : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the parent <see cref="LatePaymentPenalty"/>
    /// row whose <see cref="LatePaymentPenalty.PenaltyAmount"/> is being
    /// staggered. Required.
    /// </summary>
    public long LatePaymentPenaltyId { get; set; }

    /// <summary>
    /// Number of installments the penalty is split into. Validator enforces
    /// the inclusive (2..36) range — single-shot payments do not need a plan,
    /// and three years' worth of monthly chunks is the regulator-mandated
    /// ceiling.
    /// </summary>
    public int InstallmentCount { get; set; }

    /// <summary>
    /// Per-installment nominal amount (MDL): <c>round(PenaltyAmount /
    /// InstallmentCount, 2)</c>. Persisted as a snapshot so admin UI can chart
    /// the originally-computed value even if the underlying penalty figure is
    /// later adjusted.
    /// </summary>
    public decimal InstallmentAmount { get; set; }

    /// <summary>
    /// Statutory due date of the first installment row. Subsequent installment
    /// rows are spaced one month apart (DueDate = FirstInstallmentDueDate +
    /// (InstallmentNumber - 1) months).
    /// </summary>
    public DateOnly FirstInstallmentDueDate { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to
    /// <see cref="PenaltyRepaymentPlanStatus.Active"/>. The service mutates
    /// this field as installments are paid or the plan is defaulted /
    /// cancelled.
    /// </summary>
    public PenaltyRepaymentPlanStatus Status { get; set; } = PenaltyRepaymentPlanStatus.Active;

    /// <summary>
    /// Running count of paid installments. Incremented atomically as each
    /// installment is settled via <c>RegisterInstallmentPaymentAsync</c>. When
    /// equal to <see cref="InstallmentCount"/> the plan terminates as
    /// <see cref="PenaltyRepaymentPlanStatus.Completed"/>.
    /// </summary>
    public int PaidInstallmentCount { get; set; }

    /// <summary>
    /// Cached remainder owed (MDL) across all unpaid installment rows.
    /// Updated atomically on each payment to avoid a per-query SUM aggregate.
    /// </summary>
    public decimal RemainingAmount { get; set; }

    /// <summary>
    /// UTC instant the plan was created. Mirrors
    /// <see cref="AuditableEntity.CreatedAtUtc"/> for symmetry with the
    /// terminal stamps below — operators chart all three on the same row.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// UTC instant the plan was completed (last installment paid). Null
    /// while the plan is still in flight.
    /// </summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// UTC instant the plan was administratively cancelled. Null while the
    /// plan is not cancelled.
    /// </summary>
    public DateTime? CancelledUtc { get; set; }

    /// <summary>
    /// Operator-supplied rationale documenting the cancellation (3..500
    /// chars when set). Null while the plan is not cancelled.
    /// </summary>
    public string? CancelReason { get; set; }
}
