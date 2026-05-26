using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Rev5;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0913 / TOR BP 2.2-D — REST surface for per-insured-person contribution
/// adjustments sourced from non-REV-5 supporting documents.
/// </summary>
[ApiController]
[Authorize(Roles = "cnas-admin,cnas-user")]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class InsuredPersonAdjustmentsController : ControllerBase
{
    private readonly IInsuredPersonAdjustmentService _svc;

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">Insured-person-adjustment service façade.</param>
    public InsuredPersonAdjustmentsController(IInsuredPersonAdjustmentService svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _svc = svc;
    }

    /// <summary>R0913 / BP 2.2-D — create a new adjustment + project the personal-account entry.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the new DTO; 400/404 on failure.</returns>
    [HttpPost("api/insured-person-adjustments")]
    public async Task<ActionResult<InsuredPersonContributionAdjustmentDto>> CreateAsync(
        [FromBody] InsuredPersonContributionAdjustmentInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Created(string.Empty, result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps the service-level failure to ProblemDetails.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<InsuredPersonContributionAdjustmentDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Conflict => StatusCodes.Status409Conflict,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }
}
