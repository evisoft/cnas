using Cnas.Ps.Application.Claims;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0831 / R0832 / TOR BP 1.3-B + BP 1.3-C — REST surface for the claims
/// (creanțe) registry and the per-claim payment-application path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin,cnas-user</c> roles — citizens cannot mutate the registry.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded via
/// <see cref="ISqidService.TryDecode"/> before reaching the service layer;
/// outbound DTOs carry Sqid-encoded ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
public sealed class ClaimsController : ControllerBase
{
    private readonly IClaimService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Claim-service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public ClaimsController(IClaimService svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0831 — register a new claim against the supplied contributor.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO; 400/404 on failure.</returns>
    [HttpPost("api/claims")]
    public async Task<ActionResult<ClaimDto>> RegisterAsync(
        [FromBody] ClaimRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailureGeneric<ClaimDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0831 — modify an outstanding claim.</summary>
    /// <param name="sqid">Sqid-encoded claim id.</param>
    /// <param name="input">Validated modify payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPut("api/claims/{sqid}")]
    public async Task<ActionResult<ClaimDto>> ModifyAsync(
        string sqid,
        [FromBody] ClaimModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<ClaimDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.ModifyAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ClaimDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0831 — administratively cancel a claim with a rationale.</summary>
    /// <param name="sqid">Sqid-encoded claim id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/claims/{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        string sqid,
        [FromBody] ClaimReasonInputDto input,
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

    /// <summary>R0832 — register a payment against an existing claim.</summary>
    /// <param name="sqid">Sqid-encoded claim id.</param>
    /// <param name="input">Payment payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the payment DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/claims/{sqid}/payments")]
    public async Task<ActionResult<ClaimPaymentDto>> RegisterPaymentAsync(
        string sqid,
        [FromBody] ClaimPaymentInputDto input,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<ClaimPaymentDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.RegisterPaymentAsync(decoded.Value, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ClaimPaymentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0831 — flip a claim into the Disputed state with a rationale.</summary>
    /// <param name="sqid">Sqid-encoded claim id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/claims/{sqid}/dispute")]
    public async Task<IActionResult> DisputeAsync(
        string sqid,
        [FromBody] ClaimReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureUntyped(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var result = await _svc.DisputeAsync(decoded.Value, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureUntyped(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0831 — fetch a single claim by surrogate id.</summary>
    /// <param name="sqid">Sqid-encoded claim id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/claims/{sqid}")]
    public async Task<ActionResult<ClaimDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<ClaimDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var dto = await _svc.GetAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>R0831 — list every claim on file for the supplied contributor.</summary>
    /// <param name="contributorSqid">Sqid-encoded contributor id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the ordered list; 400 on bad Sqid.</returns>
    [HttpGet("api/contributors/{contributorSqid}/claims")]
    public async Task<ActionResult<IReadOnlyList<ClaimDto>>> ListForContributorAsync(
        string contributorSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<IReadOnlyList<ClaimDto>>(decoded.ErrorCode, decoded.ErrorMessage);
        }
        var list = await _svc.ListForContributorAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return Ok(list);
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
