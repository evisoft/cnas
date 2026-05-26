using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Financials;

/// <summary>
/// R0815 / TOR BP 1.2-F — service façade for the Treasury-payment-correction
/// workflow. Owns the <c>Draft → Approved → Applied</c> lifecycle plus the
/// cancellation path and performs the actual receipt mutation on
/// <see cref="ApplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful invocation emits a stable
/// audit event:
/// <list type="bullet">
///   <item><see cref="CreateAsync"/> → <c>PAYMENT_CORRECTION.CREATED</c> (Notice).</item>
///   <item><see cref="ApproveAsync"/> → <c>PAYMENT_CORRECTION.APPROVED</c> (Critical).</item>
///   <item><see cref="ApplyAsync"/> → <c>PAYMENT_CORRECTION.APPLIED</c> (Critical).</item>
///   <item><see cref="CancelAsync"/> → <c>PAYMENT_CORRECTION.CANCELLED</c> (Critical).</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are
/// Sqid-encoded per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public interface IPaymentCorrectionService
{
    /// <summary>
    /// R0815 — drafts a new correction row. Verifies the supplied receipt
    /// exists and that the per-kind required fields are present
    /// (RedirectToPayer → target contributor; RedirectToMonth → target
    /// month; AdjustAmount → adjusted amount &lt;= original.AmountReceived).
    /// Persists a <c>Draft</c> row and emits the
    /// <c>PAYMENT_CORRECTION.CREATED</c> Notice audit row.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="PaymentCorrectionDto"/>; on
    /// validation failure <see cref="ErrorCodes.ValidationFailed"/>; on
    /// missing receipt / redirect-target <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<PaymentCorrectionDto>> CreateAsync(PaymentCorrectionCreateInputDto input, CancellationToken ct = default);

    /// <summary>
    /// R0815 — administratively approves a correction. Refused unless the
    /// row is currently <c>Draft</c>. Stamps the approver and emits the
    /// <c>PAYMENT_CORRECTION.APPROVED</c> Critical audit row.
    /// </summary>
    /// <param name="correctionId">Raw bigint id of the correction row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> ApproveAsync(long correctionId, CancellationToken ct = default);

    /// <summary>
    /// R0815 — applies the correction to the underlying receipt. Refused
    /// unless the row is currently <c>Approved</c>. Performs the kind-
    /// dispatched mutation (Reverse / RedirectToPayer / RedirectToMonth /
    /// AdjustAmount) in the same transaction as the status flip to
    /// <c>Applied</c>. Emits the <c>PAYMENT_CORRECTION.APPLIED</c> Critical
    /// audit row.
    /// </summary>
    /// <param name="correctionId">Raw bigint id of the correction row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row / missing receipt
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> ApplyAsync(long correctionId, CancellationToken ct = default);

    /// <summary>
    /// R0815 — administratively cancels a correction. Only permitted from
    /// <c>Draft</c>. Stamps the rationale and emits the
    /// <c>PAYMENT_CORRECTION.CANCELLED</c> Critical audit row.
    /// </summary>
    /// <param name="correctionId">Raw bigint id of the correction row.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on wrong-state
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> CancelAsync(long correctionId, string reason, CancellationToken ct = default);

    /// <summary>Fetches a single correction row by surrogate id.</summary>
    /// <param name="correctionId">Raw bigint id of the correction row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <c>null</c> otherwise.</returns>
    Task<PaymentCorrectionDto?> GetAsync(long correctionId, CancellationToken ct = default);
}
