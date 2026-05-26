namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Strongly-typed configuration for the R0053 JWT-access + refresh-token pipeline
/// (CLAUDE.md §5.3 / TOR SEC 018). Bound from the <c>Jwt</c> configuration section
/// at composition root time and validated on start-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signing key discipline.</b> <see cref="SigningKey"/> is a base64-encoded
/// symmetric secret that MUST decode to ≥ 32 bytes (HS256 requires 256-bit key
/// material). The key originates in the secrets manager per CLAUDE.md §1.8 —
/// NEVER from <c>appsettings.json</c>; the composition root's
/// <c>ValidateOnStart</c> hook fails loudly if the decoded length is below 32 bytes.
/// Rotation invalidates every JWT signed with the prior key; refresh tokens are
/// unaffected because they are opaque random bytes rather than JWT signatures.
/// </para>
/// <para>
/// <b>Lifetimes.</b> <see cref="AccessTokenLifetime"/> defaults to 15 minutes
/// (SEC 018); <see cref="RefreshTokenLifetime"/> defaults to 30 days. Both are
/// configurable per environment without rebuilding.
/// </para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>Configuration section name — bound by <c>AddOptions&lt;JwtOptions&gt;().Bind(...)</c>.</summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// JWT <c>iss</c> claim value AND the value used by the JwtBearer middleware to
    /// validate incoming tokens. Must match across issuer + validator.
    /// </summary>
    public required string Issuer { get; init; }

    /// <summary>
    /// JWT <c>aud</c> claim value AND the value the JwtBearer middleware validates
    /// inbound tokens against. Typically the API's logical name (e.g. <c>cnas-api</c>).
    /// </summary>
    public required string Audience { get; init; }

    /// <summary>
    /// Base64-encoded HS256 symmetric signing key. MUST decode to ≥ 32 bytes; the
    /// composition root's startup validation rejects shorter keys with a loud
    /// <see cref="System.InvalidOperationException"/>. Sourced from the secrets
    /// manager per CLAUDE.md §1.8 — NEVER committed.
    /// </summary>
    public required string SigningKey { get; init; }

    /// <summary>
    /// Lifetime of the short-lived JWT access token. Defaults to 15 minutes per
    /// SEC 018; the refresh flow exchanges for a new access token before this window
    /// elapses.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Lifetime of an opaque refresh token. Defaults to 30 days per SEC 018; every
    /// successful rotation resets the clock for the new child token.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
}
