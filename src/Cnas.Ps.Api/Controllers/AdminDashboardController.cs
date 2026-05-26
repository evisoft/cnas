using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.AdminDashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0537 / CF 04.10 — single-payload admin dashboard read surface. Returns the
/// dashboard's superset (KPIs + recent security alerts + audit summary + open
/// admin-action backlog + optional perf metrics) in one HTTP call so the Blazor
/// admin dashboard's render path is not fan-out over N parallel REST requests.
/// </summary>
/// <remarks>
/// <para>
/// Authorisation: <see cref="AuthorizationComposition.CnasAdmin"/> — only the
/// functional administrator role (cnas-admin) reaches this endpoint. The decider
/// and user roles are not granted access; this is by design — the admin dashboard
/// summarises sensitive operational data (security alerts can reveal user activity,
/// the backlog tile reveals queue depth) that is appropriate only for the admin
/// audience.
/// </para>
/// </remarks>
/// <param name="svc">Underlying dashboard composition service.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/dashboard")]
public sealed class AdminDashboardController(IAdminDashboardService svc) : ControllerBase
{
    private readonly IAdminDashboardService _svc = svc;

    /// <summary>
    /// Composes and returns the dashboard snapshot.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the snapshot DTO on success; ProblemDetails on infrastructure failure.</returns>
    [HttpGet]
    public async Task<ActionResult<AdminDashboardDto>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Problem(result.ErrorMessage, statusCode: StatusForCode(result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>The mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
