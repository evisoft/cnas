using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — REST surface for time-bounded delegation grants.
/// All endpoints require an authenticated caller; the grantor is implicit (derived
/// from <c>ICallerContext.UserId</c> by the underlying service).
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST   /api/delegations</c>          — grant a new delegation (201 on success).</item>
///   <item><c>GET    /api/delegations</c>          — list the caller's active grants (200).</item>
///   <item><c>DELETE /api/delegations/{id}</c>     — revoke a grant the caller issued (204).</item>
/// </list>
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// every other code (<see cref="ErrorCodes.ValidationFailed"/>,
/// <see cref="ErrorCodes.InvalidSqid"/>) → 400.
/// </para>
/// </remarks>
/// <param name="svc">Underlying delegation-lifecycle service.</param>
/// <param name="grantValidator">FluentValidation validator for the grant-input DTO.</param>
/// <param name="revokeValidator">FluentValidation validator for the revoke-input DTO.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/delegations")]
public sealed class DelegationsController(
    IDelegationLifecycleService svc,
    IValidator<DelegationGrantInputDto> grantValidator,
    IValidator<DelegationGrantRevokeInputDto> revokeValidator) : ControllerBase
{
    private readonly IDelegationLifecycleService _svc = svc;
    private readonly IValidator<DelegationGrantInputDto> _grantValidator = grantValidator;
    private readonly IValidator<DelegationGrantRevokeInputDto> _revokeValidator = revokeValidator;

    /// <summary>
    /// Issues a new delegation grant from the calling user (grantor) to the supplied
    /// delegatee. The service validates the window (≤ 90 days, forward-only), the scope
    /// length, and the delegatee existence.
    /// </summary>
    /// <param name="body">Request payload carrying the delegatee + window + scope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>201 with the persisted grant on success; 400 / 401 / 404 on failure.</returns>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<DelegationGrantDto>> GrantAsync(
        [FromBody] DelegationGrantInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — invariants on the window length, scope size, and
        // forward-only direction must be enforced at the controller boundary so the
        // service never receives malformed input.
        var validation = await _grantValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.GrantAsync(
                body.DelegateeSqid,
                body.ValidFromUtc,
                body.ValidToUtc,
                body.SuspendsGrantorRights,
                body.Scope,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Created($"/api/delegations/{result.Value.Id}", result.Value)
            : MapFailureGeneric<DelegationGrantDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Lists the caller's currently-active delegation grants. A grant is active when
    /// the current UTC instant falls inside its window and the row has not been
    /// revoked.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the active grants on success; 401 / 404 on failure.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DelegationGrantDto>>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        // The caller's Sqid is the only thing the service needs to scope the query.
        // ICallerContext.UserSqid is populated by the auth pipeline; if it is absent
        // the [Authorize] attribute would already have rejected the request with 401.
        var userSqid = HttpContext.User.FindFirst("sub")?.Value
                       ?? HttpContext.User.Identity?.Name
                       ?? string.Empty;
        var result = await _svc.ListActiveAsync(userSqid, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<DelegationGrantDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Revokes a delegation grant the caller previously issued. The row is NEVER
    /// hard-deleted — revocation stamps <c>RevokedAtUtc</c> + <c>RevokeReason</c>; the
    /// row stays for audit traceability.
    /// </summary>
    /// <param name="id">Sqid-encoded id of the grant to revoke.</param>
    /// <param name="body">Request payload carrying the revocation reason.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 on success; 400 / 401 / 403 / 404 on failure.</returns>
    [HttpDelete("{id}")]
    [Consumes("application/json")]
    public async Task<IActionResult> RevokeAsync(
        [FromRoute] string id,
        [FromBody] DelegationGrantRevokeInputDto body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // FluentValidation gate — reason length must be inside the documented bounds.
        var validation = await _revokeValidator.ValidateAsync(body, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.RevokeAsync(id, body.Reason, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? NoContent()
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 401 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 403 / 401 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
