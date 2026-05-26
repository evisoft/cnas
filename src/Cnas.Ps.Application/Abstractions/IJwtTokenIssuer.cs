namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Issues short-lived JWT access tokens for authenticated callers. Lives behind the
/// R0053 token pipeline (CLAUDE.md §5.3 / SEC 018) — paired with
/// <see cref="IRefreshTokenService"/> which mints/rotates the opaque refresh token
/// the caller exchanges for a new JWT every ~15 minutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token shape.</b> Implementations produce a signed JWT carrying the standard
/// claims <c>iss</c>, <c>aud</c>, <c>sub</c> (raw internal user id — Sqid encoding
/// happens at the API boundary, never inside the access token), <c>iat</c>, <c>exp</c>,
/// plus one <c>role</c> claim per role and one <c>group</c> claim per group. Claims
/// are NEVER comma-joined — every role / group gets its own claim so downstream
/// authorization handlers can use the standard <c>HasClaim</c> APIs.
/// </para>
/// <para>
/// <b>Lifetime.</b> Default 15 minutes per SEC 018; configurable via
/// <c>JwtOptions.AccessTokenLifetime</c>. The returned <c>ExpiresAtUtc</c> aligns
/// with the JWT's <c>exp</c> claim so callers can refresh proactively without parsing
/// the token themselves.
/// </para>
/// <para>
/// <b>Stateless and thread-safe.</b> Implementations hold only the configured options
/// and a clock; register as <c>Singleton</c> at the composition root.
/// </para>
/// </remarks>
public interface IJwtTokenIssuer
{
    /// <summary>
    /// Issues a JWT access token for the given user, embedding the supplied role and
    /// group claims. Roles and groups are emitted as separate claims (one per value)
    /// rather than joined into a comma-separated string — downstream authorization
    /// handlers must be able to use the standard <c>HasClaim(type, value)</c> APIs
    /// against the resulting principal.
    /// </summary>
    /// <param name="userId">
    /// Raw internal user id. Goes into the <c>sub</c> claim as a string. Sqid encoding
    /// belongs at the API boundary; inside the JWT we need the original id so
    /// authorization handlers can look up RBAC / ABAC context without an extra decode.
    /// </param>
    /// <param name="roles">CNAS role codes to embed as <c>role</c> claims (one each).</param>
    /// <param name="groups">CNAS group codes to embed as <c>group</c> claims (one each).</param>
    /// <returns>
    /// Tuple of the signed JWT string and the UTC instant at which the token expires.
    /// The expiry value mirrors the JWT's <c>exp</c> claim so callers can refresh
    /// proactively without parsing the token themselves.
    /// </returns>
    (string Jwt, DateTime ExpiresAtUtc) IssueAccessToken(
        long userId,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> groups);
}
