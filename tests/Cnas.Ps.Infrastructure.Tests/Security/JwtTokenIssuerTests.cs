using System.IdentityModel.Tokens.Jwt;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Unit tests for <see cref="JwtTokenIssuer"/> — the JWT access-token issuer wired
/// behind <see cref="IJwtTokenIssuer"/> for the R0053 access/refresh-token pipeline
/// (SEC 018). These tests pin down the externally observable token format:
/// <list type="bullet">
///   <item>Standard claims (<c>sub</c>, <c>iat</c>, <c>exp</c>) plus per-role / per-group
///         claims are present and correct.</item>
///   <item>The default lifetime is 15 minutes (configurable but defaulted).</item>
///   <item>The issued token validates against a <see cref="TokenValidationParameters"/>
///         built from the same options — i.e. the symmetric key actually signs the JWT.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the implementation. The
/// signing key used here is a deterministic 32-byte all-zero buffer (base64-encoded)
/// for reproducibility — production reads the key from <c>JwtOptions.SigningKey</c>
/// which originates in the secrets manager. Member of
/// <see cref="CnasMeterCollection"/> — IssueAccessToken emits on the static meter.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class JwtTokenIssuerTests
{
    /// <summary>Deterministic 32-byte all-zero key, base64-encoded — sufficient for HS256 in unit tests.</summary>
    private const string SigningKeyB64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>Stable issuer string used by every test.</summary>
    private const string Issuer = "https://cnas.test";

    /// <summary>Stable audience string used by every test.</summary>
    private const string Audience = "cnas-api";

    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Test fixture user id.</summary>
    private const long UserId = 42L;

    /// <summary>
    /// Builds a configured issuer under test plus the validation parameters that match
    /// its signing key, so callers can both issue and verify in the same test.
    /// </summary>
    private static (JwtTokenIssuer Sut, TokenValidationParameters Validation, JwtOptions Options)
        BuildSut(TimeSpan? accessLifetime = null)
    {
        var options = new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = SigningKeyB64,
            AccessTokenLifetime = accessLifetime ?? TimeSpan.FromMinutes(15),
            RefreshTokenLifetime = TimeSpan.FromDays(30),
        };
        var clock = new StubClock(ClockNow);
        var sut = new JwtTokenIssuer(Options.Create(options), clock);

        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = false, // we assert exp directly via the parsed JWT
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(SigningKeyB64)),
        };
        return (sut, validation, options);
    }

    [Fact]
    public void IssueAccessToken_ReturnsValidJwt_WithExpectedClaims()
    {
        // ARRANGE
        var (sut, _, _) = BuildSut();
        string[] roles = ["cnas-admin", "cnas-user"];
        string[] groups = ["regional-chisinau"];

        // ACT
        var (jwt, _) = sut.IssueAccessToken(UserId, roles, groups);

        // ASSERT — round-trip parse the JWT and inspect its claim set.
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);

        token.Issuer.Should().Be(Issuer);
        token.Audiences.Should().ContainSingle().Which.Should().Be(Audience);

        // `sub` carries the raw internal user id — Sqid encoding happens at the API
        // boundary, never inside the access token (callers always need the raw id to
        // look up authorization context, RBAC roles, ABAC scopes, etc.).
        token.Subject.Should().Be(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // One claim per role + one per group, NEVER comma-joined.
        token.Claims.Where(c => c.Type == "role")
            .Select(c => c.Value).Should().BeEquivalentTo(roles);
        token.Claims.Where(c => c.Type == "group")
            .Select(c => c.Value).Should().BeEquivalentTo(groups);
    }

    [Fact]
    public void IssueAccessToken_DefaultLifetime_15Minutes()
    {
        // ARRANGE — default lifetime is the 15-minute SEC 018 target.
        var (sut, _, _) = BuildSut();

        // ACT
        var (jwt, expiresAtUtc) = sut.IssueAccessToken(UserId, [], []);

        // ASSERT — exp - iat ≈ 15 minutes (allow 5 second tolerance for clock-rounding inside the handler).
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);

        var lifetime = token.ValidTo - token.ValidFrom;
        lifetime.Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        // expiresAtUtc returned to the caller must align with the JWT `exp` claim.
        expiresAtUtc.Should().BeCloseTo(token.ValidTo, TimeSpan.FromSeconds(2));
        expiresAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void IssueAccessToken_SignedWithConfiguredKey_VerifiesSuccessfully()
    {
        // ARRANGE
        var (sut, validation, _) = BuildSut();

        // ACT
        var (jwt, _) = sut.IssueAccessToken(UserId, ["cnas-user"], []);

        // ASSERT — handler.ValidateToken throws when the signature does not verify; passing
        // means the symmetric key we configured really did sign the JWT.
        var handler = new JwtSecurityTokenHandler();
        var act = () => handler.ValidateToken(jwt, validation, out _);
        act.Should().NotThrow("the JWT must be signed by the configured symmetric key.");
    }

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }
}
