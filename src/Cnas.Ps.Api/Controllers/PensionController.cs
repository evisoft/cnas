using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Pension;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0514 / TOR CF 02.02 — authenticated self-service pension-projection
/// surface. Any authenticated caller (Solicitant, Utilizator autorizat,
/// administrator) may run the simulator; the caller is resolved server-side
/// via <c>ICallerContext</c>, so the route does not carry an id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defensive surface.</b> The endpoint participates in the
/// <see cref="RateLimitingPolicies.Authenticated"/> partition (per-user
/// bucket). FluentValidation owns the input-bounds contract; the controller
/// just adapts the service-level <see cref="Result{T}"/> into HTTP shapes.
/// </para>
/// <para>
/// <b>No PII.</b> The output DTO carries only the projected amount and the
/// formula breakdown — it never echoes the caller's identity.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/self-service/pension")]
public sealed class PensionController : ControllerBase
{
    private readonly IPensionCalculatorService _calculator;

    /// <summary>Constructs the controller with its service collaborator.</summary>
    /// <param name="calculator">R0514 pension-projection service.</param>
    public PensionController(IPensionCalculatorService calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        _calculator = calculator;
    }

    /// <summary>
    /// R0514 — runs the linear pension-projection simulator with the supplied
    /// inputs and returns the breakdown DTO.
    /// </summary>
    /// <param name="body">Projection variables — see DTO doc for the field-by-field contract.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the populated <see cref="PensionSimulationDto"/>; or a
    /// ProblemDetails 400 / 401 carrying the stable error code from the
    /// service-level Result.
    /// </returns>
    [HttpPost("simulate")]
    public async Task<ActionResult<PensionSimulationDto>> SimulateAsync(
        [FromBody] PensionSimulationInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var result = await _calculator
            .SimulateAsync(body, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// ProblemDetails ActionResult. <see cref="ErrorCodes.Unauthorized"/> →
    /// 401; everything else (validation, etc.) → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Pension simulation rejected.",
            Detail = errorMessage,
        };
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
