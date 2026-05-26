using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Permissions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0673 / TOR CF 18.12 — admin REST surface for the granular permission
/// matrix. Hangs off <c>/api/admin/permissions</c>; every endpoint requires
/// the <c>cnas-admin</c> policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by
/// <see cref="AuthorizationComposition.CnasAdmin"/>; the service also
/// re-checks the role as defense-in-depth. Anonymous callers receive 401;
/// authenticated non-admins receive 403.
/// </para>
/// </remarks>
/// <param name="svc">Underlying granular permission service.</param>
/// <param name="assignValidator">Validator for <see cref="GranularPermissionAssignInput"/>
/// — auto-resolved through the DI container (validators register via
/// <c>AddValidatorsFromAssemblyContaining&lt;ApplicationAssemblyMarker&gt;</c>).</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/permissions")]
public sealed class AdminPermissionsController(
    IGranularPermissionService svc,
    IValidator<GranularPermissionAssignInput> assignValidator) : ControllerBase
{
    private readonly IGranularPermissionService _svc = svc;
    private readonly IValidator<GranularPermissionAssignInput> _assignValidator = assignValidator;

    /// <summary>Lists every active permission assignment.</summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the grants; ProblemDetails on failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GranularPermissionAssignmentDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<IReadOnlyList<GranularPermissionAssignmentDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Creates a new permission assignment.</summary>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 on success; ProblemDetails on failure.</returns>
    [HttpPost]
    public async Task<ActionResult<GranularPermissionAssignmentDto>> AssignAsync(
        [FromBody] GranularPermissionAssignInput body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Input-shape validation at the boundary per CLAUDE.md §2.5. A bad
        // verb or empty role MUST surface as 400, not a 500 from the service
        // layer's defence-in-depth catch.
        var validation = await _assignValidator.ValidateAsync(body, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.AssignAsync(body.RoleCode, body.ResourceType, body.PermissionVerb, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<GranularPermissionAssignmentDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Soft-deletes the supplied permission assignment.</summary>
    /// <param name="sqid">Sqid-encoded assignment id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; ProblemDetails on failure.</returns>
    [HttpDelete("{sqid}")]
    public async Task<IActionResult> RevokeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RevokeAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        var status = StatusForCode(result.ErrorCode);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(result.ErrorMessage, statusCode: status);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type that the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Error code; null or unknown maps to 400.</param>
    /// <returns>Canonical HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.GranularPermissionUnknownRole => StatusCodes.Status400BadRequest,
        ErrorCodes.GranularPermissionUnknownVerb => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
