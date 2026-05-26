using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Penalties;

/// <summary>
/// R0819 / TOR BP 1.2-J — late-payment-penalty (majorare de întârziere)
/// calculator façade. Computes a per-day penalty on the unpaid principal of an
/// overdue <c>MonthlyContributionCalculation</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> <see cref="CalculateAsync"/> upserts on the
/// <c>(ContributorId, Month, UpToDate)</c> natural key — re-running for the
/// same triple updates the existing row in place rather than inserting a
/// duplicate. Different <c>UpToDate</c> values produce distinct rows so an
/// operator can chart penalty growth over time.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful invocation emits an Information-severity
/// audit row with stable event code <c>LATE_PENALTY.CALCULATED</c>;
/// <see cref="WaiveAsync"/> emits Critical <c>LATE_PENALTY.WAIVED</c>.
/// </para>
/// </remarks>
public interface ILatePaymentPenaltyCalculator
{
    /// <summary>
    /// R0819 / BP 1.2-J — computes the late-payment penalty for the supplied
    /// (contributor, month, up-to-date) triple. Loads the
    /// <c>MonthlyContributionCalculation</c> row for the (contributor, month)
    /// pair — when missing returns <see cref="ErrorCodes.NotFound"/> with
    /// stable message <c>MONTHLY_CALC_NOT_FOUND</c>.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the payer.</param>
    /// <param name="month">Calendar month the contribution belongs to (day = 1).</param>
    /// <param name="upToDate">Cut-off date the penalty is calculated up to (must be ≥ <paramref name="month"/>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated DTO; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on missing monthly roll-up
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<LatePaymentPenaltyDto>> CalculateAsync(
        long contributorId,
        DateOnly month,
        DateOnly upToDate,
        CancellationToken ct = default);

    /// <summary>
    /// R0819 / BP 1.2-J — admin-driven waive of an existing penalty row. Sets
    /// <c>IsWaived=true</c> and persists the rationale; the original
    /// <c>PenaltyAmount</c> is preserved (CLAUDE.md "Immutable Snapshots").
    /// Emits Critical audit <c>LATE_PENALTY.WAIVED</c>.
    /// </summary>
    /// <param name="penaltyId">Raw bigint id of the penalty row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on already-waived
    /// <see cref="ErrorCodes.Conflict"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> WaiveAsync(long penaltyId, string reason, CancellationToken ct = default);
}
