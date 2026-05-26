using Cnas.Ps.Application.Payers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — REST surface for Payer (Plătitor) linked child rows.
/// Every Sqid round-trips through <see cref="ISqidService"/>; failures map to 400/404
/// ProblemDetails via the shared mapping helpers.
/// </summary>
/// <param name="svc">Underlying linked-entities service.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[Route("api/payers/{payerSqid}")]
public sealed class PayerLinkedEntitiesController(
    IPayerLinkedEntitiesService svc,
    ISqidService sqids) : ControllerBase
{
    private readonly IPayerLinkedEntitiesService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Replaces the Payer's current address by supersession.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="input">New address payload.</param>
    /// <param name="changeReason">Optional rationale (query param).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the new (or unchanged) address; 400 on bad Sqid.</returns>
    [HttpPut("address")]
    public async Task<ActionResult<PayerAddressDto>> PutAddressAsync(
        string payerSqid,
        [FromBody] PayerAddressInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateAddressAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<PayerAddressDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Replaces the Payer's current contact by supersession.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="input">New contact payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("contact")]
    public async Task<ActionResult<PayerContactDto>> PutContactAsync(
        string payerSqid,
        [FromBody] PayerContactInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateContactAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<PayerContactDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Adds a new CAEM activity.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="input">Activity payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("activities")]
    public async Task<ActionResult<PayerActivityCaemDto>> PostActivityAsync(
        string payerSqid,
        [FromBody] PayerActivityCaemInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.AddActivityCaemAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<PayerActivityCaemDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Soft-ends an existing activity (sets <c>ValidToUtc=now</c>).</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id (kept for route consistency).</param>
    /// <param name="activitySqid">Sqid of the activity row.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("activities/{activitySqid}")]
    public async Task<IActionResult> EndActivityAsync(
        string payerSqid,
        string activitySqid,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        _ = payerSqid;
        var decoded = _sqids.TryDecode(activitySqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.EndActivityCaemAsync(decoded.Value, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? NoContent() : MapFailureBare(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Lists every historical address row for the Payer.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("addresses/history")]
    public async Task<ActionResult<IReadOnlyList<PayerAddressDto>>> ListAddressHistoryAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.ListAddressHistoryAsync(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<IReadOnlyList<PayerAddressDto>>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Lists every historical contact row.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("contacts/history")]
    public async Task<ActionResult<IReadOnlyList<PayerContactDto>>> ListContactHistoryAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.ListContactHistoryAsync(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<IReadOnlyList<PayerContactDto>>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Lists every activity row (current + historical).</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("activities/history")]
    public async Task<ActionResult<IReadOnlyList<PayerActivityCaemDto>>> ListActivityHistoryAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.ListActivityHistoryAsync(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<IReadOnlyList<PayerActivityCaemDto>>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Lists the parent-level history log for the Payer.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<PayerHistoryDto>>> ListHistoryAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var rows = await _svc.ListHistoryAsync(decoded.Value, ct).ConfigureAwait(false);
        return Ok(rows);
    }

    // ───────────────── R0803 — bank accounts ─────────────────

    /// <summary>R0803 — adds a bank account to the Payer. Sqid id round-trips through the service.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="input">Bank-account payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the new DTO; 400 on validation; 404 on bad Sqid.</returns>
    [HttpPost("bank-accounts")]
    public async Task<ActionResult<PayerBankAccountDto>> PostBankAccountAsync(
        string payerSqid,
        [FromBody] PayerBankAccountInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.AddBankAccountAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<PayerBankAccountDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>R0803 — closes a bank-account row (sets <c>ValidToUtc=now</c>).</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id (kept for route consistency).</param>
    /// <param name="bankAccountSqid">Sqid of the bank-account row to close.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("bank-accounts/{bankAccountSqid}")]
    public async Task<IActionResult> CloseBankAccountAsync(
        string payerSqid,
        string bankAccountSqid,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        _ = payerSqid;
        var decoded = _sqids.TryDecode(bankAccountSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.CloseBankAccountAsync(decoded.Value, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? NoContent() : MapFailureBare(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>R0803 — lists current bank-account rows for the Payer (primary first).</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("bank-accounts/current")]
    public async Task<ActionResult<IReadOnlyList<PayerBankAccountDto>>> ListCurrentBankAccountsAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.ListCurrentBankAccountsAsync(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<IReadOnlyList<PayerBankAccountDto>>(res.ErrorCode, res.ErrorMessage);
    }

    // ───────────────── R0803 — secondary contacts ─────────────────

    /// <summary>R0803 — adds a secondary contact (Accountant, Legal, ...) to the Payer.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="input">Secondary contact payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("secondary-contacts")]
    public async Task<ActionResult<PayerSecondaryContactDto>> PostSecondaryContactAsync(
        string payerSqid,
        [FromBody] PayerSecondaryContactInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.AddSecondaryContactAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<PayerSecondaryContactDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>R0803 — closes a secondary-contact row.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id (kept for route consistency).</param>
    /// <param name="contactSqid">Sqid of the secondary-contact row.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("secondary-contacts/{contactSqid}")]
    public async Task<IActionResult> CloseSecondaryContactAsync(
        string payerSqid,
        string contactSqid,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        _ = payerSqid;
        var decoded = _sqids.TryDecode(contactSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.CloseSecondaryContactAsync(decoded.Value, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? NoContent() : MapFailureBare(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>R0803 — lists current secondary-contact rows for the Payer.</summary>
    /// <param name="payerSqid">Sqid-encoded Payer id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("secondary-contacts/current")]
    public async Task<ActionResult<IReadOnlyList<PayerSecondaryContactDto>>> ListCurrentSecondaryContactsAsync(
        string payerSqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(payerSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.ListCurrentSecondaryContactsAsync(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<IReadOnlyList<PayerSecondaryContactDto>>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Maps a generic failure to an HTTP response.</summary>
    /// <typeparam name="T">DTO type the action returns.</typeparam>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    private ActionResult<T> MapFailure<T>(string? code, string? message) =>
        code == ErrorCodes.NotFound ? NotFound() : Problem(message, statusCode: 400);

    /// <summary>Maps a non-generic failure to an HTTP response.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    private IActionResult MapFailureBare(string? code, string? message) =>
        code == ErrorCodes.NotFound ? NotFound() : Problem(message, statusCode: 400);
}
