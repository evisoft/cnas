using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Financials;

/// <summary>
/// R0817 / TOR BP 1.2-H — service façade for the staggered-repayment workflow
/// over a <see cref="Cnas.Ps.Core.Domain.LatePaymentPenalty"/>. Owns the
/// <c>Active → Completed | Defaulted | Cancelled</c> lifecycle of the
/// underlying <see cref="Cnas.Ps.Core.Domain.PenaltyRepaymentPlan"/> plus the
/// per-installment payment-registration path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful invocation emits a stable audit
/// event:
/// <list type="bullet">
///   <item><see cref="CreatePlanAsync"/> → <c>PENALTY_PLAN.CREATED</c> (Notice).</item>
///   <item><see cref="RegisterInstallmentPaymentAsync"/> → <c>PENALTY_PLAN.INSTALLMENT_PAID</c> (Notice);
///     on the final installment additionally <c>PENALTY_PLAN.COMPLETED</c> (Critical).</item>
///   <item><see cref="CancelPlanAsync"/> → <c>PENALTY_PLAN.CANCELLED</c> (Critical).</item>
///   <item><see cref="MarkDefaultedAsync"/> → <c>PENALTY_PLAN.DEFAULTED</c> (Critical).</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are
/// Sqid-encoded per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public interface IPenaltyRepaymentService
{
    /// <summary>
    /// R0817 — creates a staggered-repayment plan for the supplied
    /// late-payment-penalty row. Verifies the penalty exists, isn't waived,
    /// and no Active plan already exists for the penalty. Generates the
    /// installment rows in a single transaction.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="PenaltyRepaymentPlanDto"/>; on
    /// validation failure <see cref="ErrorCodes.ValidationFailed"/>; on
    /// missing penalty <see cref="ErrorCodes.NotFound"/>; on
    /// waived-penalty / active-plan-exists
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<PenaltyRepaymentPlanDto>> CreatePlanAsync(
        PenaltyRepaymentCreatePlanInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0817 — registers a payment against a single installment. Marks the
    /// installment <c>IsPaid=true</c>, bumps the parent plan's
    /// <c>PaidInstallmentCount</c>, and updates <c>RemainingAmount</c>. When
    /// every installment is paid the plan transitions to
    /// <c>Completed</c> and emits the additional Critical audit row.
    /// </summary>
    /// <param name="installmentId">Raw bigint id of the installment row.</param>
    /// <param name="paidDate">Date the installment was paid; must be ≤ today.</param>
    /// <param name="paidAmount">Actual amount received (MDL, &gt; 0).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="PenaltyRepaymentInstallmentDto"/>;
    /// on validation failure <see cref="ErrorCodes.ValidationFailed"/>; on
    /// missing installment <see cref="ErrorCodes.NotFound"/>; on
    /// already-paid-installment / inactive-plan
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<PenaltyRepaymentInstallmentDto>> RegisterInstallmentPaymentAsync(
        long installmentId,
        DateOnly paidDate,
        decimal paidAmount,
        CancellationToken ct = default);

    /// <summary>
    /// R0817 — controller-facing convenience overload that resolves the
    /// installment by (planId, installmentNumber) before delegating to the
    /// raw-id <see cref="RegisterInstallmentPaymentAsync(long, DateOnly, decimal, CancellationToken)"/>
    /// path. Keeps the controller out of EF Core (CLAUDE.md §1.1 boundary
    /// rule).
    /// </summary>
    /// <param name="planId">Raw bigint id of the parent plan row.</param>
    /// <param name="installmentNumber">1-based ordinal installment position.</param>
    /// <param name="paidDate">Date the installment was paid; must be ≤ today.</param>
    /// <param name="paidAmount">Actual amount received (MDL, &gt; 0).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Same contract as the raw-id overload; additionally
    /// <see cref="ErrorCodes.NotFound"/> when the (plan, installmentNumber)
    /// tuple cannot be resolved.</returns>
    Task<Result<PenaltyRepaymentInstallmentDto>> RegisterInstallmentPaymentByNumberAsync(
        long planId,
        int installmentNumber,
        DateOnly paidDate,
        decimal paidAmount,
        CancellationToken ct = default);

    /// <summary>
    /// R0817 — administratively cancels an Active plan. Refused unless the
    /// plan is currently <c>Active</c>; emits the
    /// <c>PENALTY_PLAN.CANCELLED</c> Critical audit row.
    /// </summary>
    /// <param name="planId">Raw bigint id of the plan row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> CancelPlanAsync(long planId, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0817 — background-job entry point. Flips an Active plan to
    /// <c>Defaulted</c> when any installment is past due AND not paid for
    /// &gt; 30 days. Emits the <c>PENALTY_PLAN.DEFAULTED</c> Critical audit
    /// row.
    /// </summary>
    /// <param name="planId">Raw bigint id of the plan row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> MarkDefaultedAsync(long planId, CancellationToken ct = default);

    /// <summary>Fetches a single plan row by surrogate id.</summary>
    /// <param name="planId">Raw bigint id of the plan row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <c>null</c> otherwise.</returns>
    Task<PenaltyRepaymentPlanDto?> GetAsync(long planId, CancellationToken ct = default);
}
