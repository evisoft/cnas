using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0671 / TOR CF 18.06 — read-only REST surface exposing the caller's effective
/// access-scope envelope. Hangs off the profile namespace because the answer is
/// always "about me" — there is deliberately no admin endpoint for inspecting
/// another user's scope here (that would belong on the user-administration surface
/// once R0670 lands a richer scope-management page).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authentication required.</b> The endpoint is gated by <c>[Authorize]</c>;
/// anonymous callers receive 401 from the framework. Inside the action the
/// underlying service treats the envelope on <c>ICallerContext.AccessScope</c> as
/// authoritative, so even a misconfigured pipeline cannot leak scope info to an
/// unauthenticated caller.
/// </para>
/// <para>
/// <b>Always allowed.</b> Within the authenticated population the endpoint is
/// allowed for every role — there is no narrower RBAC gate. The descriptor IS the
/// thing the UI uses to render its scoping banner; gating it would leave staff
/// users guessing about their own scope.
/// </para>
/// </remarks>
/// <param name="scopes">Underlying access-scope service.</param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/profile/access-scope")]
public sealed class AccessScopeController(IAccessScopeService scopes) : ControllerBase
{
    private readonly IAccessScopeService _scopes = scopes;

    /// <summary>
    /// Returns the caller's effective <see cref="AccessScopeDescriptorDto"/>.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the descriptor. The service never fails today; the
    /// <see cref="Microsoft.AspNetCore.Mvc.ActionResult{T}"/> envelope is preserved
    /// for future failure modes (e.g. an admin lookup against another user id).
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<AccessScopeDescriptorDto>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _scopes.GetMineAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }
}
