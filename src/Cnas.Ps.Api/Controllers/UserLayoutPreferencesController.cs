using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UserLayout;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0535 / CF 04.07-08 — read/write surface over the authenticated caller's UI layout
/// preferences (grid column visibility / order, page-size defaults, dashboard widget
/// order). Mirrors the design of <c>ProfileController</c>'s notification-preferences
/// endpoints: caller identity is resolved server-side via <c>ICallerContext</c>, so the
/// route carries no id and there is no way for one user to read or mutate another's
/// preferences from this surface.
/// </summary>
/// <remarks>
/// <para>
/// Route table:
/// <list type="bullet">
///   <item><c>GET /api/profile/layout-preferences</c> — returns the persisted preferences
///     (or the system defaults when the column is NULL / malformed).</item>
///   <item><c>PUT /api/profile/layout-preferences</c> — replaces the preferences in full.</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="svc">Underlying layout-preferences service.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/profile/layout-preferences")]
public sealed class UserLayoutPreferencesController(IUserLayoutPreferencesService svc) : ControllerBase
{
    private readonly IUserLayoutPreferencesService _svc = svc;

    /// <summary>
    /// Reads the caller's UI layout preferences. Returns the fail-open default shape
    /// (every grid uses registry defaults, system page size, empty widget order) when
    /// the persisted JSON is NULL or malformed — see the value object remarks.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the preferences DTO.</returns>
    [HttpGet]
    public async Task<ActionResult<UserLayoutPreferencesDto>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var dto = await _svc.GetForCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        return Ok(dto);
    }

    /// <summary>
    /// Replaces the caller's UI layout preferences in full (PUT semantics — the caller
    /// is expected to send the canonical shape on every save). Validation failures
    /// surface as 400 ProblemDetails; missing user rows as 404.
    /// </summary>
    /// <param name="input">Full preferences object. Must NOT be <c>null</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the persisted DTO on success; 400 ProblemDetails on validation
    /// failure; 401 when the caller is anonymous; 404 when the user row is gone.
    /// </returns>
    [HttpPut]
    public async Task<ActionResult<UserLayoutPreferencesDto>> SaveMineAsync(
        [FromBody] UserLayoutPreferencesSaveDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _svc.SaveAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 401 / 400 ProblemDetails as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
