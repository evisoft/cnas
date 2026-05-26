using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ManagementPeriods;

/// <summary>
/// R0820 / TOR BP 1.2-K — management-period closure service. Anchors the
/// "close of management period" workflow: once a calendar month is closed, no
/// new <c>Declaration</c> rows may be registered for it unless an admin
/// re-opens the month.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton per month.</b> <see cref="CloseAsync"/> rejects a re-close with
/// <see cref="ErrorCodes.Conflict"/>. <see cref="ReopenAsync"/> flips the row
/// rather than deleting it so the audit trail is preserved.
/// </para>
/// <para>
/// <b>Audit.</b> Both <see cref="CloseAsync"/> and <see cref="ReopenAsync"/>
/// emit Critical audit rows (<c>MANAGEMENT_PERIOD.CLOSED</c> /
/// <c>MANAGEMENT_PERIOD.REOPENED</c>) because they materially alter the
/// reporting boundary and must surface in security dashboards.
/// </para>
/// <para>
/// <b>Integration with declarations.</b> The declaration-registration paths
/// consult <see cref="IsMonthClosedAsync"/> as a service-layer guard; closed
/// months refuse new declarations with
/// <c>VALIDATION_FAILED / MONTH_CLOSED</c>. A re-opened month is treated as
/// open by the probe.
/// </para>
/// </remarks>
public interface IManagementPeriodService
{
    /// <summary>
    /// R0820 / BP 1.2-K — closes the supplied calendar month. Computes the
    /// generalising-report aggregates from the monthly roll-ups and persists a
    /// new <c>ManagementPeriodClose</c> row. Emits Critical audit
    /// <c>MANAGEMENT_PERIOD.CLOSED</c>.
    /// </summary>
    /// <param name="month">Calendar month to close (day = 1).</param>
    /// <param name="notes">Optional operator note (≤ 1000 chars when supplied).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated DTO; on already-closed (and not re-opened)
    /// <see cref="ErrorCodes.Conflict"/>; on bad month
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<ManagementPeriodCloseDto>> CloseAsync(
        DateOnly month,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// R0820 / BP 1.2-K — admin-driven re-open of a closed month. Sets
    /// <c>IsReopened=true</c> on the existing close row and persists the
    /// rationale + actor + timestamp. Emits Critical audit
    /// <c>MANAGEMENT_PERIOD.REOPENED</c>.
    /// </summary>
    /// <param name="month">Calendar month to re-open (day = 1).</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing close row
    /// <see cref="ErrorCodes.NotFound"/>; on already re-opened
    /// <see cref="ErrorCodes.Conflict"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> ReopenAsync(DateOnly month, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0820 / BP 1.2-K — non-mutating probe consulted by the declaration
    /// service-layer guard. Returns <c>true</c> when the supplied month has a
    /// close row AND that row is NOT re-opened.
    /// </summary>
    /// <param name="month">Calendar month to test (day = 1).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><c>true</c> when the month is closed and not re-opened; <c>false</c> otherwise.</returns>
    Task<bool> IsMonthClosedAsync(DateOnly month, CancellationToken ct = default);

    /// <summary>
    /// R0820 / BP 1.2-K — fetches the current management-period close row for
    /// the supplied month. Returns <c>null</c> when the month has never been
    /// closed (no row exists). A re-opened month still returns its row so
    /// callers can chart the close/re-open history.
    /// </summary>
    /// <param name="month">Calendar month to look up (day = 1).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The populated DTO when a row exists; <c>null</c> otherwise.</returns>
    Task<ManagementPeriodCloseDto?> GetAsync(DateOnly month, CancellationToken ct = default);
}
