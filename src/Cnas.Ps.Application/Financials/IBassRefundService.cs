using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Financials;

/// <summary>
/// R0814 / TOR BP 1.2-E — service façade for the BASS-to-payer refund
/// workflow. Owns the <c>Requested → Approved → IssuedToTreasury →
/// Confirmed</c> lifecycle plus the cancellation path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful lifecycle invocation emits a
/// stable audit event:
/// <list type="bullet">
///   <item><see cref="RequestAsync"/> → <c>BASS_REFUND.REQUESTED</c> (Notice).</item>
///   <item><see cref="ApproveAsync"/> → <c>BASS_REFUND.APPROVED</c> (Critical).</item>
///   <item><see cref="IssueToTreasuryAsync"/> → <c>BASS_REFUND.ISSUED</c> (Critical).</item>
///   <item><see cref="ConfirmAsync"/> → <c>BASS_REFUND.CONFIRMED</c> (Critical).</item>
///   <item><see cref="CancelAsync"/> → <c>BASS_REFUND.CANCELLED</c> (Critical).</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are
/// Sqid-encoded per CLAUDE.md RULE 3; internally the service decodes them to
/// raw <see cref="long"/> primary keys before touching the DbContext.
/// </para>
/// </remarks>
public interface IBassRefundService
{
    /// <summary>
    /// R0814 — opens a new refund request for the supplied (payer, month).
    /// Requires the matching
    /// <c>Cnas.Ps.Core.Domain.MonthlyContributionCalculation.OverpaymentAmount</c>
    /// to be positive AND no non-Cancelled refund row to already exist for
    /// the same tuple.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="BassRefundDto"/>; on validation
    /// failure <see cref="ErrorCodes.ValidationFailed"/>; on missing
    /// contributor / no overpayment <see cref="ErrorCodes.NotFound"/>; on
    /// active duplicate <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<BassRefundDto>> RequestAsync(BassRefundRequestInputDto input, CancellationToken ct = default);

    /// <summary>
    /// R0814 — administratively approves a refund request. Refused unless
    /// the row is currently in
    /// <c>Cnas.Ps.Core.Domain.BassRefundStatus.Requested</c>. Stamps the
    /// approver + approved-date and emits the
    /// <c>BASS_REFUND.APPROVED</c> Critical audit row.
    /// </summary>
    /// <param name="refundId">Raw bigint id of the refund row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> ApproveAsync(long refundId, CancellationToken ct = default);

    /// <summary>
    /// R0814 — records the Treasury dispatch instruction for an Approved
    /// refund. Refused unless the row is currently
    /// <c>Cnas.Ps.Core.Domain.BassRefundStatus.Approved</c>. Stamps the
    /// dispatch reference + issued-date and emits the
    /// <c>BASS_REFUND.ISSUED</c> Critical audit row.
    /// </summary>
    /// <param name="refundId">Raw bigint id of the refund row.</param>
    /// <param name="treasuryDispatchReference">Treasury-side dispatch reference (1..64 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> IssueToTreasuryAsync(long refundId, string treasuryDispatchReference, CancellationToken ct = default);

    /// <summary>
    /// R0814 — records the Treasury confirmation that the refund landed.
    /// Refused unless the row is currently
    /// <c>Cnas.Ps.Core.Domain.BassRefundStatus.IssuedToTreasury</c>. Stamps
    /// the confirmed-date and emits the <c>BASS_REFUND.CONFIRMED</c>
    /// Critical audit row.
    /// </summary>
    /// <param name="refundId">Raw bigint id of the refund row.</param>
    /// <param name="confirmedDate">Date of confirmation; must be ≤ today.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> ConfirmAsync(long refundId, DateOnly confirmedDate, CancellationToken ct = default);

    /// <summary>
    /// R0814 — administratively cancels a refund. Only permitted from
    /// <c>Requested</c> or <c>Approved</c>; refused from
    /// <c>IssuedToTreasury</c> or later because the funds are already in
    /// flight. Stamps the rationale + cancelled-date and emits the
    /// <c>BASS_REFUND.CANCELLED</c> Critical audit row.
    /// </summary>
    /// <param name="refundId">Raw bigint id of the refund row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> CancelAsync(long refundId, string reason, CancellationToken ct = default);

    /// <summary>Fetches a single refund row by surrogate id.</summary>
    /// <param name="refundId">Raw bigint id of the refund row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <c>null</c> otherwise.</returns>
    Task<BassRefundDto?> GetAsync(long refundId, CancellationToken ct = default);
}
