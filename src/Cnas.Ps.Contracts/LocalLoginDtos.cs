using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — input DTO for the local username/password
/// login fallback. Available only to accounts that hold the <c>UtilizatorAutorizat</c>
/// role (every other persona authenticates via MPass SAML and never touches this
/// endpoint). Carried across a single HTTP request boundary and validated by
/// <c>LocalLoginInputValidator</c> before reaching the service layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity — Confidential.</b> <see cref="Password"/> is the plaintext the user
/// just typed; it MUST NEVER be persisted, logged, or echoed back in any response or
/// error message. The DTO instance is held only for the duration of the request and
/// is discarded as soon as <c>ILocalLoginService.LoginAsync</c> returns.
/// </para>
/// <para>
/// <b>Account-enumeration prevention.</b> The endpoint that consumes this DTO returns
/// the SAME stable <c>LOGIN.INVALID</c> error code for unknown login, wrong password,
/// non-Active account state, and missing <c>UtilizatorAutorizat</c> role. The wire
/// response therefore reveals nothing about which condition failed; ops dashboards
/// branch on the audit-row outcome instead (see <c>LocalLoginService</c>).
/// </para>
/// </remarks>
/// <param name="Login">
/// The user's local login handle. Validated by <c>LocalLoginInputValidator</c>:
/// 3..64 characters of <c>[a-zA-Z0-9._-]</c>. Trimmed of surrounding whitespace at the
/// validator boundary; case-insensitive comparison against
/// <c>UserProfile.LocalLogin</c>.
/// </param>
/// <param name="Password">
/// The plaintext password as typed by the user. Validated by
/// <c>LocalLoginInputValidator</c>: 8..256 characters. The validator does NOT enforce
/// the composition policy from <c>PasswordPolicyValidator</c> — that lives on the
/// change-password surface, not on login (an existing account with a legacy weak
/// password must still be able to sign in to rotate it).
/// </param>
public sealed record LocalLoginInputDto(
    string Login,
    [property: SensitivityClassification(SensitivityLabel.Restricted,
        Reason = "Plaintext password — must never appear in logs.")]
    string Password);

/// <summary>
/// R0051 / TOR SEC 014 — success envelope returned by
/// <c>POST /api/auth/token</c> when the <c>password</c> grant type succeeds against
/// a valid local-login credential. The pairing mirrors
/// <see cref="TokenResponse"/> so client code can treat both grant types uniformly:
/// store the access + refresh tokens, schedule the next refresh against the access
/// expiry. The extra metadata fields surface the resolved identity so a freshly-
/// minted client doesn't need to immediately call <c>/api/profile</c> to render the
/// "Welcome, &lt;name&gt;" greeting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity ladder.</b>
/// <list type="bullet">
///   <item><see cref="AccessToken"/> and <see cref="RefreshToken"/> — Confidential.</item>
///   <item><see cref="UserSqid"/>, <see cref="DisplayName"/>, <see cref="EffectiveRoles"/> — Internal.</item>
///   <item>Timestamps — Public (no business intelligence leaked by the issue moment alone).</item>
/// </list>
/// </para>
/// <para>
/// <b>Effective roles.</b> The list is the union of <c>UserProfile.Roles</c> and every
/// role transitively inherited through the user's group memberships (per
/// <c>IUserGroupRoleResolver.ResolveEffectiveRolesAsync</c>). The same list ends up
/// embedded as <c>role</c> claims on the JWT — surfacing it on the response is purely
/// a convenience for client-side UI so the SPA can route to the correct landing page
/// without parsing the JWT.
/// </para>
/// </remarks>
/// <param name="AccessToken">
/// HS256-signed JWT carrying the user's effective role + group claims. Lifetime 15
/// minutes per SEC 018; aligns with <see cref="AccessTokenExpiresAtUtc"/>.
/// </param>
/// <param name="AccessTokenExpiresAtUtc">
/// UTC instant after which the JWT will be rejected by the JwtBearer middleware.
/// </param>
/// <param name="RefreshToken">
/// Plaintext opaque refresh token, base64url-encoded. Single-issue — the server keeps
/// only the SHA-256 hash. The caller MUST store this securely and present it back to
/// <c>POST /api/auth/token</c> with <c>grantType=refresh_token</c> when exchanging
/// for a new access token.
/// </param>
/// <param name="RefreshTokenExpiresAtUtc">UTC expiry instant of the refresh token.</param>
/// <param name="UserSqid">Sqid-encoded id of the authenticated user (CLAUDE.md RULE 3).</param>
/// <param name="DisplayName">The user's display name (from <c>UserProfile.DisplayName</c>).</param>
/// <param name="EffectiveRoles">
/// Union of direct + group-inherited role codes; matches the JWT's <c>role</c> claim
/// set. Empty list is a valid value (a user without roles authenticated successfully
/// but cannot exercise any role-gated action).
/// </param>
public sealed record LocalLoginSuccessDto(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    string UserSqid,
    string DisplayName,
    IReadOnlyList<string> EffectiveRoles);
