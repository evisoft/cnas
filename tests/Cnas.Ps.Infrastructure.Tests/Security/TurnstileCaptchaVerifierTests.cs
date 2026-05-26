using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Unit tests for <see cref="TurnstileCaptchaVerifier"/>. Exercises every documented
/// failure mode of <c>ICaptchaVerifier</c> (missing token, provider rejection, transport
/// failure, malformed JSON, timeout) plus the <c>BypassForTesting</c> short-circuit used
/// by the E2E / integration fixtures. The handler is stubbed via the
/// <see cref="StubHttpMessageHandler"/> defined below so no real HTTP call leaks out of
/// the test process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed contract.</b> Every non-success branch that involves the upstream
/// provider must surface <see cref="ErrorCodes.CaptchaProviderUnreachable"/> so the
/// HTTP filter can map to 503 rather than letting the request through. The
/// "malformed JSON" test in particular pins this — a 200 OK with garbage body would
/// otherwise be silently treated as success.
/// </para>
/// <para>
/// <b>Bypass invariant.</b> When <c>BypassForTesting</c> is true the verifier MUST NOT
/// make an HTTP call. We assert this by inspecting the stub handler's invocation
/// counter — a regression that always calls Cloudflare from CI would waste budget and
/// flake on network outages.
/// </para>
/// </remarks>
public sealed class TurnstileCaptchaVerifierTests
{
    private const string DefaultSecret = "test-secret";
    private const string DefaultSiteKey = "test-site-key";

    private static TurnstileCaptchaVerifier BuildVerifier(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        out StubHttpMessageHandler handler,
        TurnstileOptions? options = null)
    {
        handler = new StubHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var factory = new SingleClientHttpClientFactory(http);
        var opts = Options.Create(options ?? new TurnstileOptions
        {
            SecretKey = DefaultSecret,
            SiteKey = DefaultSiteKey,
        });
        return new TurnstileCaptchaVerifier(
            factory,
            opts,
            NullLogger<TurnstileCaptchaVerifier>.Instance);
    }

    [Fact]
    public async Task VerifyAsync_NullToken_ReturnsTokenMissing()
    {
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK), out var handler);

        var result = await sut.VerifyAsync(token: null, remoteIp: "203.0.113.1");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenMissing);
        handler.Invocations.Should().Be(0, "missing-token must short-circuit without an HTTP call");
    }

    [Fact]
    public async Task VerifyAsync_EmptyToken_ReturnsTokenMissing()
    {
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK), out var handler);

        var result = await sut.VerifyAsync(token: "   ", remoteIp: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenMissing);
        handler.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_ProviderReturnsSuccessTrue_ReturnsSuccess()
    {
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                success = true,
                challenge_ts = "2026-05-21T12:00:00Z",
                hostname = "cnas.example.gov.md",
            }),
        }, out var handler);

        var result = await sut.VerifyAsync("valid-token", "203.0.113.1");

        result.IsSuccess.Should().BeTrue();
        handler.Invocations.Should().Be(1);
        handler.LastRequestUri!.AbsoluteUri.Should().Contain("siteverify");
    }

    [Fact]
    public async Task VerifyAsync_ProviderReturnsSuccessFalse_ReturnsTokenInvalid()
    {
        // Turnstile wire format uses kebab-case "error-codes" — write a literal JSON
        // string so the hyphen survives serialization (anonymous-record property names
        // are language identifiers and cannot contain hyphens).
        const string failureBody =
            """{"success":false,"error-codes":["invalid-input-response","timeout-or-duplicate"]}""";
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(failureBody, System.Text.Encoding.UTF8, "application/json"),
        }, out var _);

        var result = await sut.VerifyAsync("stale-token", "203.0.113.1");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
        // The provider error codes should appear in the human message for ops; the raw
        // token must NEVER appear (it is bearer-credential-grade).
        result.ErrorMessage.Should().Contain("invalid-input-response");
        result.ErrorMessage.Should().NotContain("stale-token");
    }

    [Fact]
    public async Task VerifyAsync_ProviderTimesOut_ReturnsProviderUnreachable()
    {
        var sut = BuildVerifier(_ => throw new TaskCanceledException("simulated upstream timeout"),
            out var _);

        var result = await sut.VerifyAsync("any-token", "203.0.113.1");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaProviderUnreachable);
        result.ErrorMessage.Should().NotContain("any-token");
    }

    [Fact]
    public async Task VerifyAsync_ProviderReturnsMalformedJson_ReturnsProviderUnreachable()
    {
        // 200 OK but body is not valid JSON — the verifier must fail CLOSED rather than
        // letting the request through. Garbage from upstream is just as dangerous as a
        // transport failure.
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json-{", System.Text.Encoding.UTF8, "application/json"),
        }, out var _);

        var result = await sut.VerifyAsync("any-token", "203.0.113.1");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaProviderUnreachable);
    }

    [Fact]
    public async Task VerifyAsync_ProviderReturns500_ReturnsProviderUnreachable()
    {
        // Upstream 5xx is the textbook transport-grade failure; fail closed.
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            out var _);

        var result = await sut.VerifyAsync("any-token", "203.0.113.1");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaProviderUnreachable);
    }

    [Fact]
    public async Task VerifyAsync_BypassForTesting_True_ReturnsSuccessWithoutHttpCall()
    {
        var sut = BuildVerifier(
            _ => throw new InvalidOperationException("bypass must not make HTTP calls"),
            out var handler,
            options: new TurnstileOptions
            {
                SecretKey = string.Empty,
                SiteKey = string.Empty,
                BypassForTesting = true,
            });

        var result = await sut.VerifyAsync("any-or-no-token", remoteIp: null);

        result.IsSuccess.Should().BeTrue();
        handler.Invocations.Should().Be(0, "bypass must short-circuit BEFORE any HTTP call");
    }

    [Fact]
    public async Task VerifyAsync_PostsFormUrlEncodedBodyWithSecretAndResponse()
    {
        // Wire-format guard: Turnstile's siteverify expects an
        // application/x-www-form-urlencoded body with secret + response keys.
        var sut = BuildVerifier(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { success = true }),
        }, out var handler);

        await sut.VerifyAsync("the-token", "203.0.113.42");

        handler.Invocations.Should().Be(1);
        handler.LastContentType.Should().Be("application/x-www-form-urlencoded");
        handler.LastBody.Should().Contain($"secret={DefaultSecret}");
        handler.LastBody.Should().Contain("response=the-token");
        handler.LastBody.Should().Contain("remoteip=203.0.113.42");
    }

    /// <summary>
    /// Minimal in-test <see cref="IHttpClientFactory"/> that always hands out the same
    /// pre-built <see cref="HttpClient"/>. The Turnstile verifier resolves its client
    /// from the factory by name, but the test path doesn't care about the named-client
    /// lifetime — we control the inner <see cref="HttpMessageHandler"/> directly.
    /// </summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>
    /// Test-only <see cref="HttpMessageHandler"/> that records every outgoing request and
    /// replies with the response produced by the supplied responder. Captures the body /
    /// content-type / URI eagerly because the verifier disposes its request before the
    /// test inspects them.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond ?? throw new ArgumentNullException(nameof(respond));
        }

        public int Invocations { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Invocations++;
            LastRequestUri = request.RequestUri;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            return _respond(request);
        }
    }
}
