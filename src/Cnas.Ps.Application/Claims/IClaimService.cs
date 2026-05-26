using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Claims;

/// <summary>
/// R0831 / R0832 / TOR BP 1.3-B + BP 1.3-C — service façade for the claims
/// (creanțe) registry. Owns the register / modify / cancel / dispute lifecycle
/// plus the per-claim payment-registration path that settles outstanding
/// obligations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful invocation emits a stable audit
/// event:
/// <list type="bullet">
///   <item><see cref="RegisterAsync"/> → <c>CLAIM.REGISTERED</c> at Notice severity.</item>
///   <item><see cref="ModifyAsync"/> → <c>CLAIM.MODIFIED</c> at Notice severity.</item>
///   <item><see cref="CancelAsync"/> → <c>CLAIM.CANCELLED</c> at Critical severity.</item>
///   <item><see cref="RegisterPaymentAsync"/> → <c>CLAIM.PAYMENT_REGISTERED</c> at Notice severity, followed by <c>CLAIM.SETTLED</c> at Critical when the final payment closes the claim.</item>
///   <item><see cref="DisputeAsync"/> → <c>CLAIM.DISPUTED</c> at Critical severity.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are Sqid-encoded
/// per CLAUDE.md RULE 3; internally the service decodes them to raw
/// <see cref="long"/> primary keys before touching the DbContext.
/// </para>
/// </remarks>
public interface IClaimService
{
    /// <summary>
    /// R0831 / BP 1.3-B — registers a new claim against the supplied
    /// contributor. Generates the <c>ClaimNumber</c> server-side in the format
    /// <c>CRN-{year}-{seq:000000}</c>, where the sequence is per-year. Emits
    /// the <c>CLAIM.REGISTERED</c> Notice audit row.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="ClaimDto"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on unknown payer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<ClaimDto>> RegisterAsync(ClaimRegisterInputDto input, CancellationToken ct = default);

    /// <summary>
    /// R0831 / BP 1.3-B — modifies the principal amount, due date or
    /// related-document reference of an outstanding claim. Refused when the
    /// claim is already in <c>Settled</c> or <c>Cancelled</c> state. Emits the
    /// <c>CLAIM.MODIFIED</c> Notice audit row.
    /// </summary>
    /// <param name="claimId">Raw bigint id of the claim row.</param>
    /// <param name="input">Modify payload (one or more fields + rationale).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the updated DTO; on cancelled/settled row
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<ClaimDto>> ModifyAsync(long claimId, ClaimModifyInputDto input, CancellationToken ct = default);

    /// <summary>
    /// R0831 / BP 1.3-B — administratively cancels an outstanding claim.
    /// Stamps <c>CancelReason</c> + <c>CancelledDate</c> and emits the
    /// <c>CLAIM.CANCELLED</c> Critical audit row.
    /// </summary>
    /// <param name="claimId">Raw bigint id of the claim row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on already-settled / already-cancelled
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> CancelAsync(long claimId, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0832 / BP 1.3-C — registers a payment against an existing claim.
    /// Atomically updates the parent's <c>PaidAmount</c> /
    /// <c>RemainingAmount</c> / <c>Status</c>. When the running total reaches
    /// the principal the claim flips to <c>Settled</c> with
    /// <c>SettledDate</c> stamped and a Critical <c>CLAIM.SETTLED</c> audit
    /// row emitted in addition to the per-payment Notice
    /// <c>CLAIM.PAYMENT_REGISTERED</c>.
    /// </summary>
    /// <param name="claimId">Raw bigint id of the parent claim.</param>
    /// <param name="input">Payment payload (date, amount, optional refs).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="ClaimPaymentDto"/>; on overpayment
    /// (would exceed <c>RemainingAmount</c>)
    /// <see cref="ErrorCodes.ValidationFailed"/> with message
    /// <c>OVERPAYMENT_NOT_ALLOWED</c>; on settled / cancelled parent
    /// <see cref="ErrorCodes.Conflict"/>; on missing claim
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<ClaimPaymentDto>> RegisterPaymentAsync(
        long claimId,
        ClaimPaymentInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0831 / BP 1.3-B — flips the claim to <c>Disputed</c>. From the
    /// Disputed state only <see cref="ModifyAsync"/> or <see cref="CancelAsync"/>
    /// are accepted; new payments are refused. Emits the <c>CLAIM.DISPUTED</c>
    /// Critical audit row.
    /// </summary>
    /// <param name="claimId">Raw bigint id of the claim row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on already-disputed / settled / cancelled
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> DisputeAsync(long claimId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Fetches a single claim by surrogate id.
    /// </summary>
    /// <param name="claimId">Raw bigint id of the claim row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <c>null</c> otherwise.</returns>
    Task<ClaimDto?> GetAsync(long claimId, CancellationToken ct = default);

    /// <summary>
    /// Lists every non-deleted claim for the supplied contributor, ordered by
    /// <c>OpenedDate</c> DESC then <c>ClaimNumber</c> ASC.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the payer.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>An ordered list — empty when the contributor has no claims on file.</returns>
    Task<IReadOnlyList<ClaimDto>> ListForContributorAsync(long contributorId, CancellationToken ct = default);
}
