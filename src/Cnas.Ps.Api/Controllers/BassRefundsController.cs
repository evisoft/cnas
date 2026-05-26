using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0814 / TOR BP 1.2-E — REST surface for the BASS-to-payer refund
/// workflow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin</c> role — refund creation, approval, dispatch and
/// confirmation are financial operations restricted to administrative
/// staff.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded via
/// <see cref="ISqidService.TryDecode"/> before reaching the service layer;
/// outbound DTOs carry Sqid-encoded ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin")]
public sealed class BassRefundsController : ControllerBase
{
    private readonly IBassRefundService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">BASS-refund service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public BassRefundsController(IBassRefundService svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0814 — open a new refund request for the (payer, month) tuple.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/bass-refunds/request")]
    public async Task<ActionResult<BassRefundDto>> RequestAsync(
        [FromBody] BassRefundRequestInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RequestAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailureGeneric<BassRefundDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0814 — administratively approve a refund request.</summary>
    /// <param name="sqid">Sqid-encoded refund id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/bass-refunds/{sqid}/approve")]
    public async Task<IActionResult> ApproveAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.ApproveAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0814 — record the Treasury dispatch instruction.</summary>
    /// <param name="sqid">Sqid-encoded refund id.</param>
    /// <param name="input">Dispatch-reference payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/bass-refunds/{sqid}/issue-to-treasury")]
    public async Task<IActionResult> IssueToTreasuryAsync(
        string sqid,
        [FromBody] BassRefundIssueInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.IssueToTreasuryAsync(
            decoded.Value, input.TreasuryDispatchReference, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0814 — confirm the Treasury wired the funds to the payer.</summary>
    /// <param name="sqid">Sqid-encoded refund id.</param>
    /// <param name="input">Confirmation-date payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/bass-refunds/{sqid}/confirm")]
    public async Task<IActionResult> ConfirmAsync(
        string sqid,
        [FromBody] BassRefundConfirmInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.ConfirmAsync(
            decoded.Value, input.ConfirmedDate, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0814 — administratively cancel a still-cancellable refund.</summary>
    /// <param name="sqid">Sqid-encoded refund id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/bass-refunds/{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        string sqid,
        [FromBody] BassRefundCancelInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.CancelAsync(decoded.Value, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0814 — fetch a single refund row by surrogate id.</summary>
    /// <param name="sqid">Sqid-encoded refund id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/bass-refunds/{sqid}")]
    public async Task<ActionResult<BassRefundDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<BassRefundDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var dto = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps non-generic-result failures to ProblemDetails (untyped action).</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails IActionResult.</returns>
    private IActionResult MapFailureUntyped(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 409 / 403 / 400 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };
}
