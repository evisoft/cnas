using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0580 / TOR CF 09.02 — REST surface for the ad-hoc report builder.
/// Hangs off <c>/api/reports/adhoc</c>; gated by the <c>cnas-user</c>
/// policy (any CNAS staff role).
/// </summary>
/// <param name="builder">Underlying ad-hoc report builder.</param>
/// <param name="validator">FluentValidation validator for the ad-hoc spec input.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/reports/adhoc")]
public sealed class AdHocReportsController(
    IAdHocReportBuilder builder,
    IValidator<AdHocReportSpecDto> validator) : ControllerBase
{
    private readonly IAdHocReportBuilder _builder = builder;
    private readonly IValidator<AdHocReportSpecDto> _validator = validator;

    /// <summary>Builds the ad-hoc report and returns the materialised rows inline.</summary>
    /// <param name="body">The ad-hoc report specification.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with <see cref="AdHocReportResultDto"/>; ProblemDetails on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<AdHocReportResultDto>> BuildAsync(
        [FromBody] AdHocReportSpecDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — invariants on entity/column lists, page/limit bounds, and
        // any other structural rules must be enforced at the controller boundary so the
        // builder never sees malformed input.
        var validation = await _validator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _builder.BuildAsync(body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps stable error codes to HTTP status codes for the ad-hoc surface.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<AdHocReportResultDto> MapFailure(string? code, string? message)
    {
        var status = code switch
        {
            ErrorCodes.AdHocReportTooLarge => StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.AdHocReportUnknownEntity => StatusCodes.Status400BadRequest,
            ErrorCodes.AdHocReportUnknownColumn => StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(message, statusCode: status);
    }
}
