using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Payers;

/// <summary>
/// R0301 / ARH 028 — service façade owning change-traceable child rows
/// (<see cref="PayerAddressDto"/>, <see cref="PayerContactDto"/>,
/// <see cref="PayerActivityCaemDto"/>, <see cref="PayerHistoryDto"/>) for a
/// <c>Contributor</c> (Plătitor). Every mutating method follows the supersession
/// pattern: closes the current row (<c>ValidToUtc = now</c>) and inserts a new row
/// (<c>ValidFromUtc = now</c>). No-op semantics apply when the incoming payload
/// is byte-equal to the current row.
/// </summary>
public interface IPayerLinkedEntitiesService
{
    /// <summary>Replaces the current address row by supersession. No-op when nothing changed.</summary>
    /// <param name="payerId">Internal Payer (Contributor) id.</param>
    /// <param name="input">New address payload.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new (or unchanged current) address as a DTO.</returns>
    Task<Result<PayerAddressDto>> UpdateAddressAsync(
        long payerId,
        PayerAddressInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Replaces the current contact row by supersession.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="input">New contact payload.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new (or unchanged current) contact as a DTO.</returns>
    Task<Result<PayerContactDto>> UpdateContactAsync(
        long payerId,
        PayerContactInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Inserts a new CAEM activity row for the Payer.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="input">New activity payload.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new activity as a DTO.</returns>
    Task<Result<PayerActivityCaemDto>> AddActivityCaemAsync(
        long payerId,
        PayerActivityCaemInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Ends an existing activity row by stamping <c>ValidToUtc=now</c>.</summary>
    /// <param name="activityId">Internal id of the activity row to close.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the row was closed; NotFound when missing or already closed.</returns>
    Task<Result> EndActivityCaemAsync(
        long activityId,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Lists every historical address row for the Payer, newest first.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Address history.</returns>
    Task<Result<IReadOnlyList<PayerAddressDto>>> ListAddressHistoryAsync(
        long payerId,
        CancellationToken ct = default);

    /// <summary>Lists every historical contact row for the Payer, newest first.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PayerContactDto>>> ListContactHistoryAsync(
        long payerId,
        CancellationToken ct = default);

    /// <summary>Lists every CAEM activity row for the Payer (current + historical), newest first.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PayerActivityCaemDto>>> ListActivityHistoryAsync(
        long payerId,
        CancellationToken ct = default);

    /// <summary>Lists every parent-level history row for the Payer, newest first.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PayerHistoryDto>> ListHistoryAsync(
        long payerId,
        CancellationToken ct = default);

    /// <summary>
    /// R0803 — appends a new bank-account row. When <paramref name="input"/>.IsPrimary
    /// is true, any existing current primary row is superseded
    /// (<c>ValidToUtc</c>=now) before the new row is inserted. Duplicate IBANs on
    /// open rows are rejected with <see cref="ErrorCodes.InvalidIban"/>.
    /// </summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="input">Bank-account payload (validated upstream).</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<PayerBankAccountDto>> AddBankAccountAsync(
        long payerId,
        PayerBankAccountInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>
    /// R0803 — closes the bank-account row identified by <paramref name="bankAccountId"/>
    /// by stamping <c>ValidToUtc</c>=now. Subsequent close calls on the same row
    /// return <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="bankAccountId">Internal id of the bank-account row.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> CloseBankAccountAsync(
        long bankAccountId,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>R0803 — lists current bank-account rows (<c>ValidToUtc IS NULL</c>) for the Payer, primary first.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PayerBankAccountDto>>> ListCurrentBankAccountsAsync(
        long payerId,
        CancellationToken ct = default);

    /// <summary>R0803 — appends a new secondary contact row.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="input">Secondary contact payload (validated upstream).</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<PayerSecondaryContactDto>> AddSecondaryContactAsync(
        long payerId,
        PayerSecondaryContactInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>R0803 — closes a secondary-contact row by stamping <c>ValidToUtc</c>=now.</summary>
    /// <param name="secondaryContactId">Internal id of the secondary-contact row.</param>
    /// <param name="changeReason">Free-text rationale (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> CloseSecondaryContactAsync(
        long secondaryContactId,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>R0803 — lists current secondary-contact rows (<c>ValidToUtc IS NULL</c>) for the Payer.</summary>
    /// <param name="payerId">Internal Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PayerSecondaryContactDto>>> ListCurrentSecondaryContactsAsync(
        long payerId,
        CancellationToken ct = default);
}
