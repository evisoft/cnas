using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Declarations;

/// <summary>
/// R0813 / TOR BP 1.2-D — monthly per-payer contribution aggregator. Sums every
/// non-cancelled <c>Declaration</c> for the (contributor, month) tuple into a
/// <c>MonthlyContributionCalculation</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> <see cref="CalculateAsync"/> upserts on the
/// <c>(ContributorId, Month)</c> natural key — re-running for the same key
/// updates the existing row in place rather than inserting a duplicate.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful invocation emits an Information-severity
/// audit row with stable event code
/// <c>CONTRIBUTOR.MONTHLY_CALC.COMPLETED</c> carrying the contributor's Sqid,
/// the month, and the resulting totals.
/// </para>
/// </remarks>
public interface IMonthlyContributionCalculator
{
    /// <summary>
    /// Recomputes the monthly contribution roll-up for the supplied
    /// (contributor, month) tuple. Reads every non-cancelled declaration for
    /// the tuple, applies the adjusted-or-declared preference, and persists a
    /// <c>MonthlyContributionCalculation</c> row.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the payer.</param>
    /// <param name="month">Calendar month (day must be 1).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated DTO; on missing payer
    /// <see cref="ErrorCodes.NotFound"/>; on bad month (day != 1)
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<MonthlyContributionCalculationDto>> CalculateAsync(
        long contributorId,
        DateOnly month,
        CancellationToken ct = default);
}
