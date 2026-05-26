using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — abstraction over the mortality table consulted by the
/// present-value annuity calculator when the underlying obligation is
/// lifetime (no <see cref="CapitalisedPaymentRequest.ObligationEndDate"/>).
/// </summary>
/// <remarks>
/// <para>
/// The production implementation (<c>MoldovaPlaceholderMortalityTable</c>)
/// ships an embedded coefficient series per sex per integer age 0..110 — the
/// values are explicitly documented as <b>placeholder</b> pending a future
/// regulatory load of the official Moldova mortality table (BNS / Biroul
/// Național de Statistică). The interface is stable; only the underlying
/// numbers change when the official table arrives.
/// </para>
/// <para>
/// <b>Failure model.</b> An age outside <c>[0, 110]</c> returns a
/// <see cref="Result{T}"/> failure with the stable code
/// <c>MORTALITY.AGE_OUT_OF_RANGE</c>. Negative ages should never reach this
/// surface (the validator rejects future birth dates), but the check is
/// defensive.
/// </para>
/// </remarks>
public interface IMortalityTable
{
    /// <summary>
    /// Returns the expected remaining life expectancy in <b>months</b> for the
    /// given biological sex and integer age. The calculator multiplies the
    /// returned month count by the per-period survival factor to discount the
    /// future payment stream.
    /// </summary>
    /// <param name="sex">Beneficiary biological sex.</param>
    /// <param name="integerAgeYears">Beneficiary age in years (integer truncation of the fractional age).</param>
    /// <returns>
    /// On success the remaining life expectancy in months (≥ 0); on out-of-
    /// range age a failure result with the stable code
    /// <c>MORTALITY.AGE_OUT_OF_RANGE</c>.
    /// </returns>
    Result<int> GetRemainingLifeExpectancyMonths(BeneficiarySex sex, int integerAgeYears);
}
