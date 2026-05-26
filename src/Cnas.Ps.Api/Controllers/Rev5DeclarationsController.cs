using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Rev5;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0910 / TOR BP 2.2-A — REST surface for the REV-5 declarations registry.
/// Four endpoints: register a new declaration with child rows, adjust a single
/// row, cancel a declaration, and fetch a declaration by id.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class Rev5DeclarationsController : ControllerBase
{
    private readonly IRev5DeclarationService _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">REV-5 declaration service façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public Rev5DeclarationsController(IRev5DeclarationService svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>R0910 / BP 2.2-A — register a REV-5 declaration with its per-employee child rows.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the new <see cref="Rev5DeclarationDto"/>; 400/404/409 on failure.</returns>
    [HttpPost("api/rev5-declarations")]
    public async Task<ActionResult<Rev5DeclarationDto>> RegisterAsync(
        [FromBody] Rev5DeclarationRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RegisterAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CreatedAtAction(
                nameof(GetAsync),
                new { sqid = result.Value.Id },
                result.Value)
            : MapFailureGeneric<Rev5DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0910 — adjust a single REV-5 row's contribution amount.</summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="nationalIdHash">IDNP hash identifying the row.</param>
    /// <param name="input">Adjustment payload (new amount + reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/rev5-declarations/{sqid}/rows/{nationalIdHash}/adjust")]
    public async Task<ActionResult<Rev5DeclarationDto>> AdjustRowAsync(
        string sqid,
        string nationalIdHash,
        [FromBody] Rev5DeclarationRowAdjustInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<Rev5DeclarationDto>(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.AdjustRowAsync(
                decoded.Value,
                nationalIdHash,
                input.AdjustedContributionAmount,
                input.Reason,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<Rev5DeclarationDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0910 — cancel a REV-5 declaration and roll back projected entries.</summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="input">Cancellation payload (reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/rev5-declarations/{sqid}/cancel")]
    public async Task<IActionResult> CancelAsync(
        string sqid,
        [FromBody] Rev5DeclarationCancelInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.CancelAsync(decoded.Value, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0910 — fetch a REV-5 declaration by surrogate id.</summary>
    /// <param name="sqid">Sqid-encoded declaration id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when found; 404 otherwise.</returns>
    [HttpGet("api/rev5-declarations/{sqid}")]
    public async Task<ActionResult<Rev5DeclarationDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureGeneric<Rev5DeclarationDto>(decoded.ErrorCode, decoded.ErrorMessage);
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

    /// <summary>Maps bare-result failures to ProblemDetails.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
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
