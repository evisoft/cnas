using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC07 — "Înregistrare formular". REST surface exposing the server-side schema-validation
/// step that runs BEFORE a workflow is started. The actual rules live on the referenced
/// <c>ServicePassport.FormSchemaJson</c>; this controller is a thin pass-through to
/// <see cref="IFormIntakeService"/> with HTTP status mapping.
/// </summary>
/// <remarks>
/// Status mapping:
/// <list type="bullet">
///   <item><description><c>200 OK</c> — payload is valid (empty body).</description></item>
///   <item><description><c>400 Bad Request</c> — payload failed validation, the supplied Sqid was malformed, or the JSON body could not be parsed; <c>detail</c> carries the human-readable reason.</description></item>
///   <item><description><c>404 Not Found</c> — the passport is missing, soft-deleted, or disabled.</description></item>
///   <item><description><c>500 Internal Server Error</c> — server-side fault (e.g., a corrupt schema saved by an administrator); a generic message is returned and the underlying detail is NOT leaked.</description></item>
/// </list>
/// </remarks>
/// <param name="svc">Form-intake validation service.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/forms")]
public sealed class FormsController(IFormIntakeService svc) : ControllerBase
{
    private readonly IFormIntakeService _svc = svc;

    /// <summary>
    /// Validate a candidate form payload against the schema declared on the referenced
    /// service passport. Returns <c>200 OK</c> on success and a <see cref="ProblemDetails"/>
    /// body on failure with the appropriate HTTP status.
    /// </summary>
    /// <param name="req">Validation request — Sqid-encoded passport id + raw JSON payload string.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>An <see cref="ActionResult"/> per the mapping documented on the controller.</returns>
    [HttpPost("validate")]
    public async Task<ActionResult> ValidateAsync(
        [FromBody] FormValidationRequest req,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var result = await _svc
            .ValidateAsync(req.ServicePassportId, req.FormPayloadJson, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Ok() : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-layer failure to the appropriate HTTP response. Internal errors
    /// emit a generic 500 message so server-side details (e.g., the literal "schema is
    /// corrupt" string) never reach API callers.
    /// </summary>
    /// <param name="code">Stable <see cref="ErrorCodes"/> value.</param>
    /// <param name="message">Human-readable detail from the service.</param>
    /// <returns>404 / 400 / 500 ProblemDetails as appropriate.</returns>
    private ActionResult MapFailure(string? code, string? message)
    {
        return code switch
        {
            ErrorCodes.NotFound => NotFound(),
            ErrorCodes.Internal => Problem(
                detail: "An internal error occurred while processing the request.",
                statusCode: StatusCodes.Status500InternalServerError),
            _ => Problem(detail: message, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}
