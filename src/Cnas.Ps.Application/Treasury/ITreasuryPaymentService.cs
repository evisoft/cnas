using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Treasury;

/// <summary>
/// R0911 / TOR BP 2.2-B — service façade for the Treasury payment-receipt
/// registry. Owns the import path (single receipt) and the per-receipt
/// distribution path that projects matching REV-5 rows into
/// <c>PersonalAccountEntry</c>.
/// </summary>
/// <remarks>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md
/// RULE 3; internally the service decodes them to raw <c>long</c> primary
/// keys. Money fields are bounded by the validator. All timestamps come from
/// <c>ICnasTimeProvider</c> — never <see cref="DateTime.UtcNow"/>.
/// </para>
/// <para>
/// <b>Distribution algorithm.</b>
/// <see cref="DistributeAsync"/> proportionally splits the
/// <c>AmountReceived</c> across every active <c>Rev5DeclarationRow</c> for
/// the receipt's (payer × reporting-month) tuple, weighted by each row's
/// <c>ContributionAmount</c>. Rows whose IDNP hash resolves to a Solicitant
/// with a personal account on file receive a <c>PersonalAccountEntry</c>
/// with <c>SourceCode = "TREASURY"</c>; the remainder (rows that cannot be
/// projected) accumulates on
/// <c>TreasuryPaymentReceipt.UndistributedRemainderAmount</c>.
/// </para>
/// </remarks>
public interface ITreasuryPaymentService
{
    /// <summary>
    /// R0911 / BP 2.2-B — imports a single Treasury payment receipt in the
    /// <c>Pending</c> state. Duplicates are rejected via the natural-key
    /// uniqueness rule on the Treasury reference number.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="TreasuryPaymentReceiptDto"/>; on
    /// duplicate natural key <see cref="ErrorCodes.Conflict"/> with stable
    /// <c>DUPLICATE_TREASURY_REFERENCE</c> in the message; on validation
    /// failure <see cref="ErrorCodes.ValidationFailed"/>; on unknown payer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<TreasuryPaymentReceiptDto>> ImportReceiptAsync(
        TreasuryPaymentReceiptImportInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0911 / BP 2.2-B — distributes a previously-imported receipt across
    /// the matching REV-5 rows and projects per-citizen
    /// <c>PersonalAccountEntry</c> rows. Idempotent for already-distributed
    /// receipts — re-invoking returns <see cref="ErrorCodes.ValidationFailed"/>
    /// with the stable <c>ALREADY_DISTRIBUTED</c> message.
    /// </summary>
    /// <param name="receiptId">Raw bigint id of the receipt.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the refreshed <see cref="TreasuryPaymentReceiptDto"/> with
    /// the terminal status; on missing receipt
    /// <see cref="ErrorCodes.NotFound"/>; on non-Pending state
    /// <see cref="ErrorCodes.ValidationFailed"/> with stable
    /// <c>ALREADY_DISTRIBUTED</c>.
    /// </returns>
    Task<Result<TreasuryPaymentReceiptDto>> DistributeAsync(
        long receiptId,
        CancellationToken ct = default);

    /// <summary>
    /// R0911 — fetches a single Treasury payment receipt by surrogate id.
    /// </summary>
    /// <param name="receiptId">Raw bigint id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the <see cref="TreasuryPaymentReceiptDto"/>; otherwise
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<TreasuryPaymentReceiptDto>> GetAsync(long receiptId, CancellationToken ct = default);
}
