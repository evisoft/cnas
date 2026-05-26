using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.RateLimiting;

/// <summary>
/// Minimal Kestrel host that mounts only the rate-limiting composition under test —
/// no DB, no MGov, no auth scheme beyond a test-only header-driven principal builder.
/// Lets us hammer endpoints quickly to verify the limiter without paying the cost of
/// booting the full Cnas.Ps.Api pipeline.
/// </summary>
internal sealed class RateLimitingTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RateLimitingTestHost(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Base address Kestrel is listening on, e.g. <c>http://127.0.0.1:54321</c>.</summary>
    public string BaseAddress { get; }

    /// <summary>Builds an <see cref="HttpClient"/> wired to the running host.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseAddress) };

    /// <summary>
    /// Boots the test host with the supplied configuration overrides. The defaults are
    /// production-like; tests pass smaller windows / smaller permit limits so the
    /// suite finishes in single-digit seconds.
    /// </summary>
    /// <param name="overrides">Configuration key/value overrides, e.g. <c>Cnas:RateLimiting:Anonymous:PermitLimit=3</c>.</param>
    public static async Task<RateLimitingTestHost> StartAsync(Dictionary<string, string?> overrides)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(overrides);

        // Test auth scheme that builds a principal from the X-Test-User header — so
        // tests can simulate different authenticated users without a real OIDC dance.
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddCnasRateLimiting(builder.Configuration);

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // --- Test endpoints --------------------------------------------------------
        // Each policy is exposed as a tiny GET so tests can fire requests without
        // having to set up controllers, DI for services, etc.
        app.MapGet("/test/anonymous", () => Results.Ok("ok"))
            .RequireRateLimiting(RateLimitingPolicies.Anonymous);

        app.MapGet("/test/callback", () => Results.Ok("ok"))
            .RequireRateLimiting(RateLimitingPolicies.Callback);

        app.MapGet("/test/authenticated", (HttpContext ctx) =>
            ctx.User?.Identity?.IsAuthenticated == true ? Results.Ok("ok") : Results.Unauthorized())
            .RequireRateLimiting(RateLimitingPolicies.Authenticated);

        app.MapGet("/test/upload", (HttpContext ctx) =>
            ctx.User?.Identity?.IsAuthenticated == true ? Results.Ok("ok") : Results.Unauthorized())
            .RequireRateLimiting(RateLimitingPolicies.Upload);

        // Health endpoint exempted from rate limiting — mirrors the production pipeline.
        app.MapGet("/health/ready", () => Results.Ok("healthy"))
            .DisableRateLimiting();

        await app.StartAsync().ConfigureAwait(false);

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var url = addresses.Addresses.First().TrimEnd('/');

        return new RateLimitingTestHost(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Test-only authentication handler that materialises a <see cref="ClaimsPrincipal"/> from
/// the <c>X-Test-User</c> header. Lets test cases simulate different authenticated callers
/// without standing up a real OIDC/SAML provider.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string HeaderName = "X-Test-User";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var userId) || string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, userId.ToString()),
            },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
