using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Cnas.Ps.Api.Filters;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Filters;

/// <summary>
/// Integration-style tests for <see cref="RequireCaptchaAttribute"/>. Mounts a minimal
/// controller decorated with <c>[RequireCaptcha]</c> behind a Kestrel host wired with
/// an overridable <see cref="ICaptchaVerifier"/> substitute. Locks the request-header
/// contract (<c>X-Captcha-Token</c>), the response-shape contract (ProblemDetails with
/// an <c>errorCode</c> extension matching the documented <see cref="ErrorCodes"/>
/// constants), and the fail-closed mapping for the unreachable branch
/// (<see cref="ErrorCodes.CaptchaProviderUnreachable"/> → HTTP 503).
/// </summary>
public sealed class RequireCaptchaAttributeTests
{
    /// <summary>JSON options matching the ProblemDetails wire format.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MissingHeader_Returns400_WithProblemDetailsErrorCode()
    {
        var verifier = Substitute.For<ICaptchaVerifier>();
        // Substitute returns Result.Success() by default for value types — but the
        // filter is supposed to short-circuit on the empty header BEFORE the verifier
        // is consulted (no verifier call should be needed if the header is absent).
        // We still program the verifier to fail just to make sure the path under
        // test is the filter's own short-circuit and not a "lucky" success.
        verifier.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.CaptchaTokenMissing, "missing"));
        await using var host = await TestHost.StartAsync(verifier);
        using var client = host.CreateClient();

        // NO X-Captcha-Token header.
        var response = await client.GetAsync("/test/captcha");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CaptchaTokenMissing);
    }

    [Fact]
    public async Task InvalidToken_Returns400()
    {
        var verifier = Substitute.For<ICaptchaVerifier>();
        verifier.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.CaptchaTokenInvalid, "rejected"));
        await using var host = await TestHost.StartAsync(verifier);
        using var client = host.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/captcha");
        req.Headers.Add("X-Captcha-Token", "2x00000000000000000000AB"); // Turnstile DEV always-fails key.
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    [Fact]
    public async Task ValidToken_Returns200_AndDownstreamRuns()
    {
        var verifier = Substitute.For<ICaptchaVerifier>();
        verifier.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        await using var host = await TestHost.StartAsync(verifier);
        using var client = host.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/captcha");
        req.Headers.Add("X-Captcha-Token", "valid");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("downstream-ran");
    }

    [Fact]
    public async Task ProviderUnreachable_Returns503()
    {
        var verifier = Substitute.For<ICaptchaVerifier>();
        verifier.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.CaptchaProviderUnreachable, "upstream down"));
        await using var host = await TestHost.StartAsync(verifier);
        using var client = host.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/captcha");
        req.Headers.Add("X-Captcha-Token", "any");
        var response = await client.SendAsync(req);

        // CLAUDE.md §5 — fail closed. Provider failure must surface as 503, not 200.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CaptchaProviderUnreachable);
    }

    /// <summary>
    /// Minimal in-process Kestrel host that mounts the <see cref="CaptchaTestController"/>
    /// and the supplied <see cref="ICaptchaVerifier"/> substitute. Mirrors the shape of
    /// <c>UnhandledExceptionMiddlewareTests.TestHost</c> so the suite stays consistent.
    /// </summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestHost(WebApplication app, string baseAddress)
        {
            _app = app;
            BaseAddress = baseAddress;
        }

        public string BaseAddress { get; }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseAddress) };

        public static async Task<TestHost> StartAsync(ICaptchaVerifier verifier)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();

            builder.Services.AddProblemDetails();
            builder.Services.AddSingleton(verifier);

            // MVC for the [RequireCaptcha]-decorated controller. We register the test
            // controller assembly's ApplicationPart explicitly so MapControllers picks
            // it up regardless of the entry-assembly default.
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(CaptchaTestController).Assembly);

            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            app.MapControllers();

            await app.StartAsync().ConfigureAwait(false);

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
            var url = addresses.Addresses.First().TrimEnd('/');

            return new TestHost(app, url);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Minimal controller used purely by <c>RequireCaptchaAttributeTests</c> to exercise the
/// <see cref="RequireCaptchaAttribute"/> filter in an end-to-end Kestrel host. Sits at
/// the top level of the file (rather than nested) so MVC's controller-discovery scan
/// finds it via the test assembly's ApplicationPart.
/// </summary>
[ApiController]
[Route("test/captcha")]
[RequireCaptcha]
public sealed class CaptchaTestController : ControllerBase
{
    /// <summary>Returns a deterministic body so tests can assert downstream ran.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { message = "downstream-ran" });
}
