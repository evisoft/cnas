using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Penalties;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0819 / TOR BP 1.2-J — REST surface for the late-payment-penalty
/// calculator. One POST endpoint runs the calculation against a payer's
/// monthly roll-up; one admin POST endpoint waives an existing penalty.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class LatePenaltyController : ControllerBase
{
    private readonly ILatePaymentPenaltyCalculator _svc;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Late-payment-penalty calculator façade.</param>
    /// <param name="sqids">Sqid encoder/decoder for route parameters.</param>
    public LatePenaltyController(ILatePaymentPenaltyCalculator svc, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(sqids);
        _svc = svc;
        _sqids = sqids;
    }

    /// <summary>
    /// R0819 / BP 1.2-J — calculate the late penalty for a payer's overdue
    /// contribution. Idempotent on the (contributor, month, up-to-date) natural key.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded payer id.</param>
    /// <param name="input">Calculation input (month, up-to-date).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the populated DTO; 400/404 on failure.</returns>
    [HttpPost("api/contributors/{contributorSqid}/late-penalty/calculate")]
    public async Task<ActionResult<LatePaymentPenaltyDto>> CalculateAsync(
        string contributorSqid,
        [FromBody] LatePaymentPenaltyCalculateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return MapFailure(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.CalculateAsync(decoded.Value, input.Month, input.UpToDate, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0819 / BP 1.2-J — admin path that waives an existing penalty.
    /// </summary>
    /// <param name="sqid">Sqid-encoded penalty id.</param>
    /// <param name="input">Waive payload (reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/late-penalties/{sqid}/waive")]
    [Authorize(Roles = "cnas-admin")]
    public async Task<IActionResult> WaiveAsync(
        string sqid,
        [FromBody] LatePaymentPenaltyWaiveInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return MapFailureBare(decoded.ErrorCode, decoded.ErrorMessage);
        }

        var result = await _svc.WaiveAsync(decoded.Value, input.Reason, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<LatePaymentPenaltyDto> MapFailure(string? code, string? message)
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
