using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2267 / SEC 020 — self-service session lock + active-session listing surface, plus
/// the admin force-terminate path. Two route prefixes are hosted by this single
/// controller class:
/// <list type="bullet">
///   <item><description><c>/api/profile/lock-session</c>, <c>/unlock-session</c>, <c>/active-sessions</c> — authenticated, self-service.</description></item>
///   <item><description><c>/api/admin/users/{userSqid}/terminate-session/{sessionSqid}</c> — CnasAdmin-only.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The authenticated endpoints accept any <c>[Authorize]</c> principal — the
/// underlying service resolves the row via <c>ICallerContext.SessionId</c> so the
/// route never carries an opaque id. The admin endpoint is additionally gated by
/// the <see cref="AuthorizationComposition.CnasAdmin"/> policy via the per-action
/// attribute.
/// </para>
/// </remarks>
/// <param name="svc">Session lock service implementing the underlying operations.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
public sealed class SessionsController(ISessionLockService svc) : ControllerBase
{
    private readonly ISessionLockService _svc = svc;

    /// <summary>
    /// Locks the caller's current session — the user clicked "step away from this
    /// device". Subsequent authenticated requests bound to the same session id are
    /// refused by middleware until the user unlocks.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated session DTO; 401 when no session id is present.</returns>
    [HttpPost("/api/profile/lock-session")]
    public async Task<ActionResult<UserSessionDto>> LockSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.LockCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<UserSessionDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Unlocks the caller's current session — symmetric to
    /// <see cref="LockSessionAsync"/>. Honoured even when the session is already
    /// unlocked (idempotent success).
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the updated session DTO; 401 when no session id is present.</returns>
    [HttpPost("/api/profile/unlock-session")]
    public async Task<ActionResult<UserSessionDto>> UnlockSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.UnlockCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<UserSessionDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Lists the caller's currently-active sessions (IsTerminated=false), newest
    /// first. Renders the "where am I signed in" view in the self-service profile.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the list; 401 when the caller is anonymous.</returns>
    [HttpGet("/api/profile/active-sessions")]
    public async Task<ActionResult<IReadOnlyList<UserSessionDto>>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListMineAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<IReadOnlyList<UserSessionDto>>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Admin force-terminate of a specific user's session. Writes an audit Critical
    /// row (<c>USER.SESSION.ADMIN_TERMINATED</c>).
    /// </summary>
    /// <param name="userSqid">Sqid-encoded id of the session owner.</param>
    /// <param name="sessionSqid">Sqid-encoded id of the session row to kill.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 on success; 400 when either sqid is malformed; 403 when the caller is
    /// not a CnasAdmin (the per-action attribute below enforces this); 404 when
    /// the session row cannot be located.
    /// </returns>
    [HttpPost("/api/admin/users/{userSqid}/terminate-session/{sessionSqid}")]
    [Authorize(Policy = AuthorizationComposition.CnasAdmin)]
    public async Task<IActionResult> AdminTerminateAsync(
        [FromRoute] string userSqid,
        [FromRoute] string sessionSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.AdminTerminateAsync(userSqid, sessionSqid, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a generic <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type that the action would have returned on success.</typeparam>
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
    /// <param name="code">Error code; null or unknown maps to 400.</param>
    /// <returns>404 NotFound, 403 Forbidden, 401 Unauthorized, or 400 BadRequest.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
