using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.E2E.Tests.Auth;

/// <summary>
/// E2E-only <see cref="AuthenticationHandler{TOptions}"/> that materialises a
/// <see cref="ClaimsPrincipal"/> from the <c>X-Test-User</c> request header so authenticated
/// journey tests can act as any persona without standing up a real MPass OIDC dance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Header shape.</b> Tests send <c>X-Test-User</c> with a JSON document — see
/// <see cref="TestPrincipal"/> — describing the persona. Example:
/// <c>{"sub":"k3Gq9","roles":["cnas-admin","cnas-decider"],"idnp":"2000000000007"}</c>.
/// The <c>sub</c> claim becomes both the Sqid-encoded user id (HttpCallerContext reads
/// either the <c>uid</c> claim or <see cref="ClaimTypes.NameIdentifier"/>) and the rate
/// limiter's partition key (the limiter resolves <see cref="ClaimTypes.NameIdentifier"/>
/// directly — see <c>RateLimitingComposition.ResolveUserPartitionKey</c>).
/// </para>
/// <para>
/// <b>Scheme name.</b> The handler registers itself under
/// <see cref="SchemeName"/> — a distinct name from the production cookie scheme — and
/// the <see cref="ApiHostFixture"/> re-points the default authenticate/challenge scheme
/// at it via <c>PostConfigure&lt;AuthenticationOptions&gt;</c> when
/// <c>Cnas:E2E:TestAuth:Enabled</c> is <c>true</c>. <c>[Authorize]</c> resolves the
/// test principal through the rewired default scheme without ever touching the
/// production cookie / OIDC handlers.
/// </para>
/// <para>
/// <b>No header → no result.</b> When the header is absent or empty,
/// <see cref="AuthenticateResult.NoResult"/> is returned so unauthenticated requests
/// (e.g. anonymous public endpoints) keep working — the handler is non-intrusive.
/// </para>
/// <para>
/// <b>Role claim type.</b> CNAS authorization policies use <see cref="ClaimTypes.Role"/>
/// per <c>AuthenticationComposition.AddCnasAuthentication</c> — the production OIDC
/// pipeline maps MPass <c>role</c> / <c>roles</c> / <c>groups</c> claims onto
/// <see cref="ClaimTypes.Role"/>. This handler mirrors that contract so role-based
/// policies (CnasUser / CnasDecider / CnasAdmin) and <c>[Authorize(Roles="…")]</c>
/// attributes both resolve correctly.
/// </para>
/// </remarks>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>HTTP header carrying the JSON persona document.</summary>
    public const string HeaderName = "X-Test-User";

    /// <summary>
    /// Scheme name under which this handler is registered. Deliberately distinct from
    /// the production cookie scheme — the fixture re-points the default authenticate
    /// scheme at this name via <c>PostConfigure&lt;AuthenticationOptions&gt;</c> so
    /// <c>[Authorize]</c> resolves the test principal without colliding with the
    /// already-registered <see cref="CookieAuthenticationDefaults.AuthenticationScheme"/>.
    /// </summary>
    public const string SchemeName = "Cnas.E2E.Test";

    /// <summary>Reusable JSON parser configuration (camel-case, case-insensitive properties).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Constructs the handler. Parameters are supplied by ASP.NET Core's authentication
    /// infrastructure via the standard <see cref="AuthenticationBuilder"/> registration.
    /// </summary>
    /// <param name="options">Options monitor — defaults are sufficient; no custom options bound.</param>
    /// <param name="logger">Logger factory used for diagnostic messages on malformed headers.</param>
    /// <param name="encoder">URL encoder (unused here; required by the base class).</param>
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Reads the <see cref="HeaderName"/> header, parses it as JSON, and materialises a
    /// <see cref="ClaimsPrincipal"/> carrying the same claim types the production MPass
    /// pipeline emits. Absent / empty / malformed headers yield
    /// <see cref="AuthenticateResult.NoResult"/> so other registered schemes (or anonymous
    /// access) can still proceed.
    /// </summary>
    /// <returns>
    /// <see cref="AuthenticateResult.Success(AuthenticationTicket)"/> with the materialised
    /// ticket when the header parses cleanly; <see cref="AuthenticateResult.NoResult"/>
    /// otherwise.
    /// </returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var raw) || raw.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var headerValue = raw.ToString();
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        TestPrincipal? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TestPrincipal>(headerValue, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Malformed headers are a test-author bug — log at Warning so the failure is
            // visible in the xUnit output without blocking unrelated suites.
            Logger.LogWarning(ex, "Malformed {Header} header in E2E request: {Raw}", HeaderName, headerValue);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Claim composition mirrors the production MPass pipeline:
        //   * "uid"  — Sqid string read first by HttpCallerContext.UserSqid
        //   * ClaimTypes.NameIdentifier — Sqid string (fallback), also the rate
        //     limiter's user-partition key
        //   * ClaimTypes.Name — display name for auditing
        //   * ClaimTypes.Role — one entry per granted CNAS role code
        //   * "idnp" — citizen IDNP (used by some service-layer flows)
        //   * "mpower:principal_idnp" — MPower delegation principal (optional)
        //   * "mpower:delegation_id" — MPower delegation id (optional)
        var claims = new List<Claim>
        {
            new("uid", parsed.Sub),
            new(ClaimTypes.NameIdentifier, parsed.Sub),
            new(ClaimTypes.Name, parsed.DisplayName ?? parsed.Sub),
        };
        if (parsed.Roles is not null)
        {
            foreach (var role in parsed.Roles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(parsed.Idnp))
        {
            claims.Add(new Claim("idnp", parsed.Idnp));
        }
        if (!string.IsNullOrWhiteSpace(parsed.OnBehalfOfPrincipalIdnp))
        {
            claims.Add(new Claim("mpower:principal_idnp", parsed.OnBehalfOfPrincipalIdnp));
        }
        if (!string.IsNullOrWhiteSpace(parsed.DelegationId))
        {
            claims.Add(new Claim("mpower:delegation_id", parsed.DelegationId));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// JSON shape of the <see cref="TestAuthHandler.HeaderName"/> header. Properties match
/// the persona attributes the test author needs to set; everything is optional except
/// <see cref="Sub"/>, which doubles as the user's Sqid id and the rate limiter's
/// partition key.
/// </summary>
/// <param name="Sub">
/// Sqid-encoded user id — written into both the <c>uid</c> claim and
/// <see cref="ClaimTypes.NameIdentifier"/>. Required.
/// </param>
/// <param name="DisplayName">
/// Optional display name; falls through to <see cref="Sub"/> when omitted. Mirrors what
/// the MPass <c>name</c> / <c>preferred_username</c> claim populates in production.
/// </param>
/// <param name="Roles">
/// Optional list of CNAS role codes (<c>cnas-user</c>, <c>cnas-decider</c>,
/// <c>cnas-admin</c>, ...). Each becomes a <see cref="ClaimTypes.Role"/> claim.
/// </param>
/// <param name="Idnp">
/// Optional 13-digit Moldovan IDNP — written as an <c>idnp</c> claim. Some service-layer
/// flows (e.g. delegated-submission verification) read it.
/// </param>
/// <param name="OnBehalfOfPrincipalIdnp">
/// Optional principal IDNP for MPower-delegated submissions — written as
/// <c>mpower:principal_idnp</c>, consumed by <c>HttpCallerContext.OnBehalfOfPrincipalIdnp</c>.
/// </param>
/// <param name="DelegationId">
/// Optional opaque MPower delegation id — written as <c>mpower:delegation_id</c>,
/// consumed by <c>HttpCallerContext.DelegationPowerId</c>.
/// </param>
public sealed record TestPrincipal(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("name")] string? DisplayName = null,
    [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles = null,
    [property: JsonPropertyName("idnp")] string? Idnp = null,
    [property: JsonPropertyName("onBehalfOfPrincipalIdnp")] string? OnBehalfOfPrincipalIdnp = null,
    [property: JsonPropertyName("delegationId")] string? DelegationId = null);
