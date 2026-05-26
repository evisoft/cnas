using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.ManagementPeriods;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0820 / TOR BP 1.2-K — REST surface for the management-period close /
/// re-open lifecycle. One POST endpoint closes a month, one POST endpoint
/// (admin) re-opens a closed month, one GET endpoint returns the current
/// close state.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class ManagementPeriodController : ControllerBase
{
    private readonly IManagementPeriodService _svc;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Management-period service façade.</param>
    public ManagementPeriodController(IManagementPeriodService svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _svc = svc;
    }

    /// <summary>R0820 / BP 1.2-K — close the supplied management period (calendar month).</summary>
    /// <param name="month">Calendar month to close (parsed from <c>YYYY-MM-DD</c>; day = 1).</param>
    /// <param name="input">Optional payload carrying close notes.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the populated DTO; 400/409 on failure.</returns>
    [HttpPost("api/management-period/{month}/close")]
    public async Task<ActionResult<ManagementPeriodCloseDto>> CloseAsync(
        DateOnly month,
        [FromBody] ManagementPeriodCloseInputDto? input,
        CancellationToken cancellationToken = default)
    {
        var notes = input?.Notes;
        var result = await _svc.CloseAsync(month, notes, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0820 / BP 1.2-K — admin re-open of a previously closed management period.</summary>
    /// <param name="month">Calendar month to re-open (day = 1).</param>
    /// <param name="input">Re-open payload (reason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404/409 on failure.</returns>
    [HttpPost("api/management-period/{month}/reopen")]
    [Authorize(Roles = "cnas-admin")]
    public async Task<IActionResult> ReopenAsync(
        DateOnly month,
        [FromBody] ManagementPeriodReopenInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.ReopenAsync(month, input.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R0820 / BP 1.2-K — get the current close state for a management period.</summary>
    /// <param name="month">Calendar month to look up (day = 1).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO when a row exists; 404 otherwise.</returns>
    [HttpGet("api/management-period/{month}")]
    public async Task<ActionResult<ManagementPeriodCloseDto>> GetAsync(
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var dto = await _svc.GetAsync(month, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<ManagementPeriodCloseDto> MapFailure(string? code, string? message)
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
