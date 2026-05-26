using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Request DTO for <c>POST /api/auth/token</c> — the R0053 token-issue endpoint
/// (CLAUDE.md §5.3 / SEC 018).
/// </summary>
/// <remarks>
/// <para>
/// The endpoint currently supports a single grant type: <c>refresh_token</c>. The
/// <c>password</c> grant (local username + password fallback for the
/// <c>Utilizator autorizat</c> role) belongs to R0051 and intentionally returns
/// HTTP 501 from this batch — callers passing <c>password</c> get a "not yet
/// implemented" signal rather than a malformed-request error.
/// </para>
/// </remarks>
/// <param name="GrantType">
/// OAuth2-style grant identifier. Recognised values: <c>refresh_token</c> (R0053)
/// and <c>password</c> (R0051 — local username/password fallback for the
/// <c>UtilizatorAutorizat</c> persona). Anything else returns 400.
/// </param>
/// <param name="RefreshToken">
/// Opaque refresh token previously issued by the server. Required when
/// <see cref="GrantType"/> is <c>refresh_token</c>; ignored otherwise.
/// </param>
/// <param name="Login">
/// Local login handle. Required when <see cref="GrantType"/> is <c>password</c>;
/// ignored otherwise. Validated by <c>LocalLoginInputValidator</c>.
/// </param>
/// <param name="Password">
/// Plaintext password. Required when <see cref="GrantType"/> is <c>password</c>;
/// ignored otherwise. NEVER persisted; the service hashes the candidate via
/// Argon2id and compares against <c>UserProfile.LocalPasswordHash</c>.
/// </param>
public sealed record IssueTokenRequest(
    string GrantType,
    string? RefreshToken,
    string? Login = null,
    [property: SensitivityClassification(SensitivityLabel.Restricted,
        Reason = "Plaintext password — must never appear in logs.")]
    string? Password = null);

/// <summary>
/// Response DTO for <c>POST /api/auth/token</c> — carries the freshly minted JWT
/// access token alongside its rotated opaque refresh-token counterpart.
/// </summary>
/// <param name="AccessToken">
/// HS256-signed JWT carrying the user's role + group claims. Lifetime 15 minutes
/// per SEC 018.
/// </param>
/// <param name="AccessTokenExpiresAtUtc">
/// UTC instant after which the JWT will be rejected by the JwtBearer middleware.
/// Aligns with the JWT's <c>exp</c> claim so the caller does not need to parse the
/// token to schedule the next refresh.
/// </param>
/// <param name="RefreshToken">
/// Freshly-rotated opaque refresh token. The caller MUST replace its previously-stored
/// refresh token with this value — failure to do so will trigger reuse-detection on
/// the next rotation, killing both the legitimate session and any compromised copy.
/// </param>
/// <param name="RefreshTokenExpiresAtUtc">
/// UTC instant after which the refresh token will be rejected by
/// <c>POST /api/auth/token</c>. Lifetime 30 days per SEC 018; the clock resets on
/// every successful rotation.
/// </param>
public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

/// <summary>
/// Request DTO for <c>POST /api/auth/logout</c> — revokes the entire refresh-token
/// family the supplied token belongs to. Idempotent: unknown tokens silently
/// succeed.
/// </summary>
/// <param name="RefreshToken">
/// Opaque refresh token whose family should be revoked. Empty / whitespace strings
/// short-circuit to HTTP 400 at the controller boundary; everything else flows
/// through to the service and is treated as a no-op on unknown values.
/// </param>
public sealed record LogoutRequest(string RefreshToken);
