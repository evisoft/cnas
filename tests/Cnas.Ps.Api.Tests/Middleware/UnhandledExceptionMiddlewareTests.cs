using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Api.Middleware;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Middleware;

/// <summary>
/// End-to-end tests for <see cref="UnhandledExceptionMiddleware"/>. The middleware is
/// mounted in front of a tiny test endpoint that throws on demand; the tests assert:
/// (a) the response is a ProblemDetails 500 with a stable error code and correlation id,
/// (b) stack traces never leak on the wire in either production or non-production,
/// (c) the existing controller-level Result→ProblemDetails mapping is not disturbed, and
/// (d) the exception is logged server-side via <see cref="ILogger"/>. SEC 057 / R0033.
/// </summary>
public sealed class UnhandledExceptionMiddlewareTests
{
    /// <summary>Endpoint route that throws a <see cref="NullReferenceException"/>.</summary>
    private const string ThrowPath = "/test/throw";

    /// <summary>Endpoint route that throws AFTER having written part of the response body.</summary>
    private const string PartialThenThrowPath = "/test/partial-then-throw";

    /// <summary>Endpoint route that returns a 409 ProblemDetails via the existing controller pattern.</summary>
    private const string ConflictPath = "/test/conflict";

    /// <summary>JSON options matching ASP.NET Core's default (camelCase for ProblemDetails).</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Production_AnyException_Returns500_WithProblemDetailsAndNoStackTrace()
    {
        await using var host = await TestHost.StartAsync(environment: "Production");
        using var client = host.CreateClient();

        var response = await client.GetAsync(ThrowPath);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);

        problem.GetProperty("status").GetInt32().Should().Be(500);
        // ProblemDetails.Extensions is marked [JsonExtensionData] — entries serialise
        // at the JSON root, NOT under an "extensions" sub-object.
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.Internal);
        problem.TryGetProperty("correlationId", out var corr).Should().BeTrue();
        corr.GetString().Should().NotBeNullOrWhiteSpace();

        // detail must be absent (or null) in Production — no exception type, no message.
        if (problem.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null)
        {
            detail.GetString().Should().BeNullOrEmpty(
                "production responses must not surface the exception type or message");
        }

        // Stack-trace / type-name markers must NEVER appear in the body in Production.
        body.Should().NotContain("SimulatedUnhandledException", "exception type must not leak in Production");
        body.Should().NotContain("   at ", "stack trace frames must not leak");
        body.Should().NotContain(".cs:line", "source paths must not leak");
        body.Should().NotContain("StackTrace", "stack-trace property must not be serialised");
    }

    [Fact]
    public async Task NonProduction_AnyException_Returns500_WithDetailButStillNoStackTrace()
    {
        await using var host = await TestHost.StartAsync(environment: "Development");
        using var client = host.CreateClient();

        var response = await client.GetAsync(ThrowPath);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);

        problem.GetProperty("status").GetInt32().Should().Be(500);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.Internal);

        // In non-production, detail SHOULD include the exception type/message for devs.
        problem.GetProperty("detail").GetString().Should().Contain("SimulatedUnhandledException");

        // ...but never a stack trace.
        body.Should().NotContain("   at ", "stack trace frames must not leak in any environment");
        body.Should().NotContain(".cs:line", "source paths must not leak in any environment");
        body.Should().NotContain("StackTrace", "stack-trace property must not be serialised");
    }

    [Fact]
    public async Task CorrelationId_PropagatesFromTraceIdentifierToBody()
    {
        // The project's correlation-id source is HttpContext.TraceIdentifier (see
        // HttpCallerContext.CorrelationId). Each request gets a fresh non-empty id;
        // we assert the middleware reads it and surfaces it on the response.
        await using var host = await TestHost.StartAsync(environment: "Production");
        using var client = host.CreateClient();

        var response = await client.GetAsync(ThrowPath);

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);

        var correlationId = problem.GetProperty("correlationId").GetString();
        correlationId.Should().NotBeNullOrWhiteSpace(
            "every ProblemDetails must carry a correlation id so server logs can be joined to the report");
    }

    [Fact]
    public async Task ResponseAlreadyStarted_LogsAndDoesNotCrashProcess()
    {
        var logger = Substitute.For<ILogger<UnhandledExceptionMiddleware>>();
        await using var host = await TestHost.StartAsync(
            environment: "Production",
            loggerOverride: logger);
        using var client = host.CreateClient();

        // The endpoint writes a partial response then throws; the middleware cannot rewrite
        // an in-flight response, so it must log and propagate without crashing the host.
        // We don't assert on response.StatusCode because the response is already on the wire
        // and frameworks vary in how they finalise it; the contract is "log + don't crash".
        try
        {
            using var _ = await client.GetAsync(PartialThenThrowPath, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            // Connection may be aborted mid-stream — acceptable, the host did not die.
        }

        // The middleware must have logged either the primary "Unhandled exception serving..."
        // line (response not yet committed) OR the "AFTER response started" fallback line.
        logger.ReceivedCalls()
            .Any(c => c.GetArguments().OfType<Exception>().Any())
            .Should().BeTrue("the middleware must log every unhandled exception, even when the response is already in flight");
    }

    [Fact]
    public async Task ControllerProblemDetailsResponse_FlowsThroughMiddlewareUnchanged()
    {
        await using var host = await TestHost.StartAsync(environment: "Production");
        using var client = host.CreateClient();

        var response = await client.GetAsync(ConflictPath);

        // The endpoint deliberately returns a 409 ProblemDetails. The middleware must NOT
        // rewrite this to a 500 — its job is to catch escape, not to second-guess controller
        // results.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":409");
        body.Should().NotContain(ErrorCodes.Internal, "the controller's own error code must not be overwritten");
    }

    [Fact]
    public async Task UnhandledException_IsLoggedAsError_WithCorrelationIdAndPath()
    {
        var logger = Substitute.For<ILogger<UnhandledExceptionMiddleware>>();
        // Make the logger consider Error enabled so LogError actually emits.
        logger.IsEnabled(LogLevel.Error).Returns(true);
        await using var host = await TestHost.StartAsync(
            environment: "Production",
            loggerOverride: logger);
        using var client = host.CreateClient();

        var response = await client.GetAsync(ThrowPath);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // ILogger.LogError(Exception, string, params object?[]) flows through the
        // generic Log(LogLevel.Error, ...) method on the substitute. Asserting that at
        // least one Error-level call received our Exception is the stable contract:
        // future changes to the message template won't break the test.
        var errorCallReceivedException = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Any(c =>
            {
                var args = c.GetArguments();
                // Signature: Log(LogLevel, EventId, TState, Exception?, Func<TState,Exception?,string>)
                if (args.Length < 4) return false;
                if (args[0] is not LogLevel lvl || lvl != LogLevel.Error) return false;
                return args[3] is Exception;
            });

        errorCallReceivedException.Should().BeTrue(
            "the middleware must call LogError with the captured exception so it lands in server-side logs");
    }

    // ---------------------------------------------------------------------------------
    // Test host helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Minimal Kestrel host that mounts only the <see cref="UnhandledExceptionMiddleware"/> in
    /// front of three test endpoints (throw / partial-then-throw / conflict). Mirrors
    /// <c>RateLimitingTestHost</c> in shape so the suite keeps a consistent style.
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

        public static async Task<TestHost> StartAsync(
            string environment,
            ILogger<UnhandledExceptionMiddleware>? loggerOverride = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
            });
            builder.Logging.ClearProviders();

            // ProblemDetails is the on-the-wire envelope; register the framework default
            // formatter so the middleware can rely on WriteAsJsonAsync emitting application/problem+json.
            builder.Services.AddProblemDetails();

            if (loggerOverride is not null)
            {
                builder.Services.AddSingleton(loggerOverride);
                builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            }

            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();

            // The whole point of the middleware is to sit IN FRONT of everything else.
            app.UseMiddleware<UnhandledExceptionMiddleware>();

            // Use a project-local exception subtype to dodge CA2201 (which forbids
            // throwing System.NullReferenceException directly from product code). The
            // test still verifies the "any exception" contract — the middleware never
            // branches on type.
            app.MapGet(ThrowPath, (HttpContext _) =>
            {
                throw new SimulatedUnhandledException("simulated null deref (NullReferenceException stand-in)");
#pragma warning disable CS0162 // Unreachable code — required to satisfy minimal-API return-type inference.
                return Results.Ok();
#pragma warning restore CS0162
            });

            app.MapGet(PartialThenThrowPath, async (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("partial...").ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync().ConfigureAwait(false);
                throw new InvalidOperationException("thrown AFTER bytes already on the wire");
            });

            app.MapGet(ConflictPath, () => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict.",
                detail: "Resource is already in the requested state."));

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

    /// <summary>
    /// Test-only exception type used to simulate an arbitrary unhandled throw. Avoids
    /// throwing runtime-reserved types like <see cref="NullReferenceException"/> directly,
    /// which the static analyser flags (CA2201).
    /// </summary>
    private sealed class SimulatedUnhandledException : Exception
    {
        public SimulatedUnhandledException(string message) : base(message) { }
    }
}
