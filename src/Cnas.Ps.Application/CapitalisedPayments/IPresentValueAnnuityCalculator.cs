using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — pure-function present-value annuity calculator. Given
/// a fractional age, a monthly indemnity amount, a valuation date, an optional
/// fixed obligation end, and an annual legal discount rate, returns the
/// present value of the future stream of monthly indemnity payments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// <list type="number">
///   <item>Compute monthly effective discount: <c>(1 + r/100)^(1/12) - 1</c>.</item>
///   <item>Determine the number of payment periods N (months from
///         valuation date to obligation end if set, otherwise mortality-table
///         remaining life expectancy at <c>floor(age)</c>).</item>
///   <item>For each period <c>t = 1..N</c> accumulate
///         <c>MonthlyAmount * survivalProb(t) * 1 / (1 + monthlyDiscount)^t</c>.</item>
///   <item>Round the total to 2 decimals (banker's rounding).</item>
/// </list>
/// </para>
/// <para>
/// <b>Survival model.</b> For lifetime obligations a linear decrement is used
/// (<c>survivalProb(t) = (N - t + 1) / N</c>). This is a placeholder until full
/// annuity tables are loaded, and is documented inside the breakdown payload.
/// Fixed-end obligations use <c>survivalProb(t) = 1</c>.
/// </para>
/// <para>
/// <b>No side effects.</b> The interface is intentionally <c>Result&lt;T&gt;</c>-
/// returning rather than <c>Task</c>-returning — the computation is pure,
/// allocation-light, and CPU-bound; tests benefit from synchronous semantics.
/// </para>
/// </remarks>
public interface IPresentValueAnnuityCalculator
{
    /// <summary>
    /// Computes the present value of the future monthly indemnity stream.
    /// </summary>
    /// <param name="input">Validated calculator input.</param>
    /// <returns>
    /// On success the
    /// <see cref="CapitalisedAnnuityComputationDto"/> envelope; on validation
    /// failure or mortality-table miss a failure result with the appropriate
    /// stable error code.
    /// </returns>
    Result<CapitalisedAnnuityComputationDto> Compute(CapitalisedAnnuityInputDto input);
}
