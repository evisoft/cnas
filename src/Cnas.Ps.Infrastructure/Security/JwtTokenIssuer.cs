using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// <see cref="IJwtTokenIssuer"/> implementation that mints HS256-signed JWT access
/// tokens for the R0053 token pipeline (CLAUDE.md §5.3 / TOR SEC 018). Stateless and
/// thread-safe — registered as <c>Singleton</c> at the composition root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim shape.</b> The token carries the standard claims <c>iss</c>, <c>aud</c>,
/// <c>sub</c> (raw internal user id — Sqid encoding happens at the API boundary,
/// never inside the access token so downstream authorization handlers can look up
/// RBAC / ABAC context without an extra decode), <c>iat</c>, <c>exp</c>, plus one
/// <c>role</c> claim per role and one <c>group</c> claim per group. Claims are NEVER
/// comma-joined — every value gets its own claim so the standard
/// <see cref="ClaimsPrincipal.HasClaim(string, string)"/> APIs work directly.
/// </para>
/// <para>
/// <b>Signing key validation.</b> The constructor reads the configured base64
/// signing key once; the composition root's <c>ValidateOnStart</c> hook is responsible
/// for rejecting keys that decode to less than 32 bytes (HS256 needs ≥256-bit key
/// material). The implementation itself does NOT re-validate length on every call so
/// the hot path stays allocation-free.
/// </para>
/// </remarks>
public sealed class JwtTokenIssuer : IJwtTokenIssuer
{
    /// <summary>JWT options snapshot (issuer, audience, key, lifetimes).</summary>
    private readonly JwtOptions _options;

    /// <summary>Clock abstraction — every <c>iat</c> / <c>exp</c> instant flows through here.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>Pre-built signing credentials so each issue call avoids re-allocating.</summary>
    private readonly SigningCredentials _signingCredentials;

    /// <summary>
    /// Constructs the issuer with the bound JWT options and the system clock. Reads
    /// the configured signing key once and caches the corresponding
    /// <see cref="SigningCredentials"/> so the hot path stays allocation-free.
    /// </summary>
    /// <param name="options">JWT options bound from the <c>Jwt</c> configuration section.</param>
    /// <param name="clock">System clock abstraction (per CLAUDE.md cross-cutting — UTC Everywhere).</param>
    public JwtTokenIssuer(IOptions<JwtOptions> options, ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options.Value;
        _clock = clock;

        var keyBytes = Convert.FromBase64String(_options.SigningKey);
        var symmetricKey = new SymmetricSecurityKey(keyBytes);
        _signingCredentials = new SigningCredentials(symmetricKey, SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public (string Jwt, DateTime ExpiresAtUtc) IssueAccessToken(
        long userId,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> groups)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(groups);

        var issuedAt = _clock.UtcNow;
        var expiresAt = issuedAt + _options.AccessTokenLifetime;

        // Build the claim list explicitly so we control the type-name and emit one entry
        // per role/group rather than letting JwtSecurityToken auto-coalesce identically
        // typed claims. We use the short names "role" and "group" (matching MPass's
        // existing convention — see AuthenticationComposition.RoleClaimTypes) so callers
        // do not need to special-case multiple claim-type spellings.
        var claims = new List<Claim>(2 + roles.Count + groups.Count)
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(issuedAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
        }
        foreach (var group in groups)
        {
            claims.Add(new Claim("group", group));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expiresAt,
            signingCredentials: _signingCredentials);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        // R0040 — issuance counter. Increment AFTER the JWT writer has finished so a
        // serialization throw is NOT counted as success. Tagless: the rate alone is
        // the operator signal; per-user/per-role tags would bust the cardinality
        // budget (CLAUDE.md §5.6) and leak business intelligence.
        CnasMeter.JwtAccessIssued.Add(1);

        // Re-pin the expiry to UTC explicitly — DateTime arithmetic preserves Kind when
        // both operands are UTC, but the explicit assertion documents the contract.
        return (jwt, DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc));
    }
}
