namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0817 / TOR BP 1.2-H — single installment row attached to a parent
/// <see cref="PenaltyRepaymentPlan"/>. <c>N</c> rows are generated when the
/// plan is created (one per installment), and operators flip
/// <see cref="IsPaid"/> as each installment is settled via
/// <c>RegisterInstallmentPaymentAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascade delete.</b> Configured with <c>OnDelete(DeleteBehavior.Cascade)</c>
/// — when the parent plan row is hard-deleted (rare; mostly through migration
/// rollbacks) every child installment row is removed in the same transaction.
/// In normal operation the plan is soft-deleted via the inherited
/// <see cref="AuditableEntity.IsActive"/> flag and the installments survive.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> A composite unique index on
/// (<see cref="PenaltyRepaymentPlanId"/>, <see cref="InstallmentNumber"/>)
/// guarantees that each (plan × position) pair appears exactly once.
/// </para>
/// </remarks>
public sealed class PenaltyRepaymentInstallment : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the parent <see cref="PenaltyRepaymentPlan"/>
    /// this installment belongs to. Required.
    /// </summary>
    public long PenaltyRepaymentPlanId { get; set; }

    /// <summary>
    /// 1-based ordinal position within the parent plan (e.g. 1, 2, ..., N).
    /// Participates in the composite uniqueness index together with
    /// <see cref="PenaltyRepaymentPlanId"/>.
    /// </summary>
    public int InstallmentNumber { get; set; }

    /// <summary>
    /// Statutory due date for this installment — derived from the parent
    /// plan's <c>FirstInstallmentDueDate</c> plus
    /// (<see cref="InstallmentNumber"/> - 1) calendar months.
    /// </summary>
    public DateOnly DueDate { get; set; }

    /// <summary>
    /// Nominal amount owed for this installment (MDL). For 1..(N-1) this
    /// equals the plan's <c>InstallmentAmount</c>; for the final row it
    /// absorbs any rounding residual so the plan-level total reconciles
    /// exactly to the parent penalty's <c>PenaltyAmount</c>.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Date the installment was paid. Null while
    /// <see cref="IsPaid"/> is <c>false</c>.
    /// </summary>
    public DateOnly? PaidDate { get; set; }

    /// <summary>
    /// Actual amount received for this installment (MDL). May differ slightly
    /// from <see cref="Amount"/> if the payer over/under-paid; the service
    /// records the actual figure for audit reproducibility. Null while
    /// <see cref="IsPaid"/> is <c>false</c>.
    /// </summary>
    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// Settlement flag — flipped to <c>true</c> when
    /// <c>RegisterInstallmentPaymentAsync</c> records the payment for this
    /// installment row.
    /// </summary>
    public bool IsPaid { get; set; }
}
