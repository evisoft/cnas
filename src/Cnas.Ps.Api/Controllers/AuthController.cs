using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0053 token + logout surface (CLAUDE.md §5.3 / SEC 018). Anonymous + rate-limited
/// because the caller may not yet have any session credentials when they ask for
/// their first access token, but the endpoints touch the credential pipeline and
/// therefore need the strictest abuse-prevention guards.
/// </summary>
/// <remarks>
/// <para>
/// Two endpoints today:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>POST /api/auth/token</c> — exchanges a refresh token for a fresh JWT
///       access token + a rotated refresh token. The <c>password</c> grant type
///       (local username/password) belongs to R0051 and is intentionally surfaced
///       as HTTP 501 here so callers wiring against the eventual login flow get a
///       clear "not yet implemented" signal rather than a misleading 400.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>POST /api/auth/logout</c> — revokes the entire refresh-token family
///       the supplied token belongs to. Idempotent — unknown tokens return 204 so
///       a malicious client cannot infer token existence by probing logout.
///     </description>
///   </item>
/// </list>
/// <para>
/// Both endpoints use the <see cref="RateLimitingPolicies.Anonymous"/> policy —
/// IP-partitioned, 5 req/min — matching CLAUDE.md §5.3 "rate limiting on auth
/// endpoints (5 req/min)".
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IRefreshTokenService _refreshSvc;
    private readonly IJwtTokenIssuer _jwtIssuer;
    private readonly ILocalLoginService _localLoginSvc;

    /// <summary>
    /// Constructs the controller. All three dependencies are resolved at activation
    /// time so the password-grant path (R0051) can dispatch to the local-login
    /// service without an additional <c>IServiceProvider</c> hop.
    /// </summary>
    /// <param name="refreshSvc">Refresh-token service (mint / rotate / revoke).</param>
    /// <param name="jwtIssuer">JWT access-token issuer.</param>
    /// <param name="localLoginSvc">Local username/password login service (R0051).</param>
    public AuthController(
        IRefreshTokenService refreshSvc,
        IJwtTokenIssuer jwtIssuer,
        ILocalLoginService localLoginSvc)
    {
        ArgumentNullException.ThrowIfNull(refreshSvc);
        ArgumentNullException.ThrowIfNull(jwtIssuer);
        ArgumentNullException.ThrowIfNull(localLoginSvc);
        _refreshSvc = refreshSvc;
        _jwtIssuer = jwtIssuer;
        _localLoginSvc = localLoginSvc;
    }

    /// <summary>
    /// Exchanges a refresh token for a freshly-minted access + rotated refresh token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Currently honours one grant type — <c>refresh_token</c>. The <c>password</c>
    /// grant returns HTTP 501 (Not Implemented) because the local-login flow is the
    /// scope of R0051; returning 400 would imply the request was malformed, which
    /// would be misleading. Anything else maps to 400.
    /// </para>
    /// <para>
    /// On reuse-detection (an already-consumed refresh token presented again), the
    /// refresh service revokes the entire family server-side; this action surfaces
    /// HTTP 401 to the client without distinguishing reuse from other 401 cases at
    /// the wire (the stable error code on the ProblemDetails body lets ops dashboards
    /// branch on the specific cause).
    /// </para>
    /// </remarks>
    /// <param name="body">Token-issue request body.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with a <see cref="TokenResponse"/> on success; 400 (missing token / unknown
    /// grant), 401 (invalid / expired / revoked / reused refresh token), or 501
    /// (password grant — deferred to R0051).
    /// </returns>
    [HttpPost("token")]
    [Consumes("application/json")]
    public async Task<ActionResult<TokenResponse>> TokenAsync(
        [FromBody] IssueTokenRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Branch on grant type. Reject unknown / unsupported types BEFORE touching the
        // refresh service so we cannot accidentally rotate on a malformed request.
        switch (body.GrantType)
        {
            case "refresh_token":
                return await HandleRefreshTokenGrantAsync(body, cancellationToken).ConfigureAwait(false);

            case "password":
                return await HandlePasswordGrantAsync(body, cancellationToken).ConfigureAwait(false);

            default:
                return Problem(
                    detail: $"Unsupported grant type '{body.GrantType}'. " +
                            "Valid values: refresh_token.",
                    statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Revokes the entire refresh-token family the supplied token belongs to. Idempotent
    /// — unknown tokens still return 204 so a malicious client cannot infer token
    /// existence by probing logout responses.
    /// </summary>
    /// <param name="body">Logout request body.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>204 No Content on success; 400 when the token field is empty/whitespace.</returns>
    [HttpPost("logout")]
    [Consumes("application/json")]
    public async Task<IActionResult> LogoutAsync(
        [FromBody] LogoutRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // The service treats whitespace tokens as a no-op success too, but enforcing the
        // 400 at the controller layer surfaces obvious client bugs quickly (an empty body
        // is far more likely to be a client-side wiring mistake than a deliberate logout).
        if (string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return Problem(
                detail: "RefreshToken is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _refreshSvc.RevokeFamilyAsync(body.RefreshToken, "logout", cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Inner handler for the <c>refresh_token</c> grant. Pulled out of
    /// <see cref="TokenAsync"/> so the grant-type switch stays single-concern.
    /// </summary>
    /// <param name="body">Request body — <c>RefreshToken</c> must be supplied.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with token response, 400 missing token, or 401 on any refresh failure.</returns>
    private async Task<ActionResult<TokenResponse>> HandleRefreshTokenGrantAsync(
        IssueTokenRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return Problem(
                detail: "RefreshToken is required when grantType=refresh_token.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rotated = await _refreshSvc.RotateAsync(body.RefreshToken, ct).ConfigureAwait(false);
        if (rotated.IsFailure)
        {
            // Every refresh-rotation failure maps to 401 at the wire — the precise cause
            // is carried by the stable error code on the ProblemDetails body. Reuse,
            // invalid, expired, revoked all collapse to "Unauthorized" from the client's
            // perspective because they all mean "this token will not get you in".
            return Problem(
                detail: rotated.ErrorMessage ?? "Refresh token rejected.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Issue the new access token. The refresh service stamped the user id on the
        // RefreshTokenIssueResult so we can mint the JWT against the same identity
        // without re-querying. Roles + groups stay empty for now — the local-login
        // flow lands with R0051, which will project UserProfile.Roles / .Groups onto
        // the claim set. The current empty-claim JWT is sufficient to validate the
        // bearer pipeline and to authorise any policy whose only requirement is
        // <c>RequireAuthenticatedUser</c>.
        // Note: TokenResponse pairs the new access token's expiry with the rotated
        // refresh token's expiry so the caller can schedule its next exchange without
        // parsing either token.
        var (jwt, accessExpires) = _jwtIssuer.IssueAccessToken(
            userId: rotated.Value.UserId,
            roles: Array.Empty<string>(),
            groups: Array.Empty<string>());

        return Ok(new TokenResponse(
            AccessToken: jwt,
            AccessTokenExpiresAtUtc: accessExpires,
            RefreshToken: rotated.Value.OpaqueToken,
            RefreshTokenExpiresAtUtc: rotated.Value.ExpiresAtUtc));
    }

    /// <summary>
    /// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — inner handler for the
    /// <c>password</c> grant. Dispatches to <see cref="ILocalLoginService.LoginAsync"/>
    /// after the controller-level rate-limit (5 req/min per IP, anonymous policy).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every recognised failure mode — unknown login, wrong password, non-Active
    /// account, missing <c>UtilizatorAutorizat</c> role — collapses to HTTP 400
    /// with the stable <see cref="ErrorCodes.LoginInvalid"/> code on the
    /// ProblemDetails body. The wire response therefore reveals NOTHING about
    /// which condition failed (account-enumeration prevention); ops dashboards
    /// branch on the per-outcome audit row written by the service.
    /// </para>
    /// <para>
    /// The successful response maps the <see cref="LocalLoginSuccessDto"/> envelope
    /// — which carries identity metadata (user sqid, display name, effective
    /// roles) alongside the token pair — onto the wire body. Client code can use
    /// the metadata to render the post-login greeting without an immediate
    /// <c>/api/profile</c> round-trip.
    /// </para>
    /// </remarks>
    /// <param name="body">Request body — <c>Login</c> + <c>Password</c> must be supplied.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with <see cref="LocalLoginSuccessDto"/>, or 400 with <c>LOGIN.INVALID</c>.</returns>
    private async Task<ActionResult<TokenResponse>> HandlePasswordGrantAsync(
        IssueTokenRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Login) || string.IsNullOrWhiteSpace(body.Password))
        {
            // Surface the missing-field shape with the SAME LOGIN.INVALID error code
            // the service would have returned — clients see one stable code on every
            // bad-credential path so they don't have to special-case missing fields.
            return Problem(
                detail: "Invalid credentials.",
                statusCode: StatusCodes.Status400BadRequest,
                title: ErrorCodes.LoginInvalid);
        }

        // Capture the inbound IP + user-agent at the controller boundary — the
        // service never reads HttpContext directly because it lives in
        // Infrastructure (which has no reference to Microsoft.AspNetCore.Http).
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

        var input = new LocalLoginInputDto(body.Login, body.Password);
        var result = await _localLoginSvc.LoginAsync(input, ip, userAgent, ct).ConfigureAwait(false);
        if (result.IsFailure || result.Value is null)
        {
            // Every recognised failure collapses to 400 with the LOGIN.INVALID body.
            // Non-LOGIN.INVALID codes (e.g. INTERNAL_ERROR from token issuance)
            // surface as 500 so ops dashboards can chart genuine outages distinctly.
            var status = result.ErrorCode == ErrorCodes.LoginInvalid
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status500InternalServerError;
            return Problem(
                detail: result.ErrorMessage ?? "Invalid credentials.",
                statusCode: status,
                title: result.ErrorCode ?? ErrorCodes.LoginInvalid);
        }

        // Map the local-login envelope onto the shared TokenResponse shape so
        // clients can handle refresh_token + password grants with one code path.
        // The richer LocalLoginSuccessDto is available to clients that want the
        // identity metadata; surfacing it via Ok(value) preserves the additional
        // fields under the contract (TokenResponse covariance is by-design).
        var v = result.Value;
        return Ok(new LocalLoginSuccessDto(
            AccessToken: v.AccessToken,
            AccessTokenExpiresAtUtc: v.AccessTokenExpiresAtUtc,
            RefreshToken: v.RefreshToken,
            RefreshTokenExpiresAtUtc: v.RefreshTokenExpiresAtUtc,
            UserSqid: v.UserSqid,
            DisplayName: v.DisplayName,
            EffectiveRoles: v.EffectiveRoles));
    }
}
