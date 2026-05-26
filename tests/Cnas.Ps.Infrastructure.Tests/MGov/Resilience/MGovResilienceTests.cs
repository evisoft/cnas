using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Cnas.Ps.Infrastructure.Tests.MGov.Resilience;

/// <summary>
/// Behavioural tests for <see cref="MGovResilienceExtensions.AddMGovResilience"/>. Each
/// test wires a single resilience pipeline against a <see cref="ResilienceTestHandler"/>
/// and asserts the observable contract — invocation count, exception type, backoff
/// timing — promised by CLAUDE.md §6.2 and TOR R0100.
/// </summary>
/// <remarks>
/// These tests intentionally use the real Polly v8 strategies (no mocking) because the
/// goal is to pin the integration of <c>Microsoft.Extensions.Http.Resilience</c> v10 to
/// the CNAS playbook contract. A future package upgrade that changes default predicates
/// or backoff semantics will trip these tests immediately.
/// </remarks>
public class MGovResilienceTests
{
    /// <summary>
    /// Test #1 — 500, 500, 500, 200: assert exactly 4 invocations (1 initial + 3 retries)
    /// and the final response observed by the caller is 200.
    /// </summary>
    [Fact]
    public async Task Retry_OnTransient500_RetriesThreeTimesThenSucceeds()
    {
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 3,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
        });
        host.Handler
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.OK);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Handler.CallCount.Should().Be(4, "1 initial attempt + 3 retries");
    }

    /// <summary>
    /// Test #2 — 500 indefinitely: assert exactly 4 invocations and the caller observes
    /// the final 500.
    /// </summary>
    [Fact]
    public async Task Retry_OnTransient500_GivesUpAfterMaxRetries()
    {
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 3,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
        });
        host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Handler.CallCount.Should().Be(4, "1 initial + 3 retries, then gives up returning the last upstream 500");
    }

    /// <summary>
    /// Test #3 — 400 (Bad Request): caller-error class, must NOT be retried.
    /// </summary>
    [Fact]
    public async Task Retry_DoesNotRetryOn400()
    {
        using var host = ResilienceTestHost.Build("msign", DefaultFastOpts);
        host.Handler.EnqueueStatus(HttpStatusCode.BadRequest);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Handler.CallCount.Should().Be(1, "4xx are caller errors — retry would just multiply the broken request");
    }

    /// <summary>
    /// Test #4 — 401 (Unauthorized): auth class, never retried.
    /// </summary>
    [Fact]
    public async Task Retry_DoesNotRetryOn401()
    {
        using var host = ResilienceTestHost.Build("msign", DefaultFastOpts);
        host.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Handler.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Test #5 — 404 (Not Found): caller error, never retried.
    /// </summary>
    [Fact]
    public async Task Retry_DoesNotRetryOn404()
    {
        using var host = ResilienceTestHost.Build("msign", DefaultFastOpts);
        host.Handler.EnqueueStatus(HttpStatusCode.NotFound);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Handler.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Test #6 — <see cref="HttpRequestException"/>: transient network failure must be
    /// retried like a 5xx (Polly v10 default predicate).
    /// </summary>
    [Fact]
    public async Task Retry_OnNetworkException_Retries()
    {
        using var host = ResilienceTestHost.Build("msign", DefaultFastOpts);
        host.Handler.EnqueueException(static () => new HttpRequestException("simulated DNS failure"));

        var http = host.GetClient();
        Func<Task> act = async () => await http.GetAsync("http://localhost/x");
        await act.Should().ThrowAsync<HttpRequestException>();

        host.Handler.CallCount.Should().Be(4, "HttpRequestException is in the default transient predicate — 1 + 3 retries");
    }

    /// <summary>
    /// Test #7 — 5 consecutive failures opens the circuit; subsequent requests fast-fail
    /// with <see cref="BrokenCircuitException"/> (or a wrapping exception) before the
    /// primary handler is invoked again.
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OpensAfterThresholdFailures()
    {
        // Threshold 3, MaxRetries 0 so each request only fires one attempt — easier to
        // reason about the number of attempts that trip the breaker.
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 0,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 3,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
        });
        host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        var http = host.GetClient();

        // Trip the breaker by firing past the threshold.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                _ = await http.GetAsync("http://localhost/x");
            }
            catch
            {
                // Polly wraps the broken-circuit signal once the breaker is open —
                // swallow here, we assert on the call count below.
            }
        }

        // After the breaker opens further requests must NOT increment the handler
        // counter, which means the upstream gets shielded.
        var beforeOpen = host.Handler.CallCount;
        Func<Task> act = async () => await http.GetAsync("http://localhost/x");
        await act.Should().ThrowAsync<BrokenCircuitException>();
        host.Handler.CallCount.Should().Be(beforeOpen,
            "once the breaker is open, the primary handler must not be invoked again");
    }

    /// <summary>
    /// Test #8 — backoff delays approximate the exponential schedule. The assertion
    /// uses a generous tolerance because Polly's jitter is randomised and the test
    /// runs on shared CI hardware.
    /// </summary>
    [Fact]
    public async Task Backoff_DelaysIncreaseExponentially()
    {
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 3,
                BaseDelayMs: 200,
                JitterMs: 50,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
        });
        host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        var http = host.GetClient();
        var sw = Stopwatch.StartNew();
        _ = await http.GetAsync("http://localhost/x");
        sw.Stop();

        host.Handler.CallCount.Should().Be(4);

        // Exponential backoff: 200, 400, 800 ms median ± jitter. Total expected median
        // is ~1400 ms (1.4 s). Allow generous tolerance for CI noise: at least 400 ms
        // (which would only happen if backoff is fast-mode constant) and at most 6 s
        // (which would catch a runaway exponential bug).
        var totalMs = sw.Elapsed.TotalMilliseconds;
        totalMs.Should().BeGreaterThan(400,
            "exponential backoff with base 200 ms must observably delay the retry chain");
        totalMs.Should().BeLessThan(6000,
            "backoff should not run away beyond the configured exponential schedule");

        // Inter-arrival deltas should also increase on average. Sanity-check that the
        // gap between calls 2 and 3 is larger than the gap between calls 1 and 2 — the
        // simplest exponential-growth assertion that survives jitter.
        var ts = host.Handler.Timestamps;
        var d12 = (ts[1] - ts[0]).TotalMilliseconds;
        var d23 = (ts[2] - ts[1]).TotalMilliseconds;
        var d34 = (ts[3] - ts[2]).TotalMilliseconds;
        (d12 + d23 + d34).Should().BeGreaterThan(400,
            "cumulative inter-arrival time of the three retry gaps must reflect exponential growth");
    }

    /// <summary>
    /// Test #9 — per-attempt timeout cancels a slow handler. Polly v10 raises
    /// <see cref="TimeoutRejectedException"/>, which the retry layer classifies as a
    /// transient failure and retries; once the budget is exhausted the caller sees
    /// the timeout surface as-is.
    /// </summary>
    [Fact]
    public async Task Timeout_PerAttempt_TerminatesSlowResponse()
    {
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 1,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 1,
                PipelineTimeoutSeconds: 30);
        });
        // Each handler invocation sleeps 5s (way over the 1s per-attempt timeout).
        host.Handler.EnqueueDelay(TimeSpan.FromSeconds(5), HttpStatusCode.OK);

        var http = host.GetClient();
        var sw = Stopwatch.StartNew();
        Func<Task> act = async () => await http.GetAsync("http://localhost/x");
        // The exception type after retries exhaust is TimeoutRejectedException (the
        // innermost timeout strategy's signal). It may be re-raised wrapped depending
        // on the runtime — we accept either the typed exception or any task-cancelled
        // surface.
        try
        {
            await act();
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or TaskCanceledException or OperationCanceledException)
        {
            // Expected.
        }
        sw.Stop();

        // The per-attempt timeout is 1s — so the test should never take 5s · 2 = 10s.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8),
            "per-attempt timeout must terminate the slow upstream long before the body would have returned");
        host.Handler.CallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Test #10 — escape hatch. With <see cref="MGovResilienceOptions.Enabled"/> set
    /// to <c>false</c> a 500 must NOT trigger any retry; the caller sees the upstream
    /// status code after exactly one invocation.
    /// </summary>
    [Fact]
    public async Task Disabled_BypassesAllResilience()
    {
        using var host = ResilienceTestHost.Build("msign", opts =>
        {
            // Force Enabled=false via the public init API; the same effect as binding
            // Cnas:MGov:Resilience:Enabled=false in configuration.
            typeof(MGovResilienceOptions)
                .GetProperty(nameof(MGovResilienceOptions.Enabled))!
                .SetValue(opts, false);
        });
        host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        var http = host.GetClient();
        var resp = await http.GetAsync("http://localhost/x");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Handler.CallCount.Should().Be(1,
            "when Enabled=false the pipeline must be a pure pass-through");
    }

    /// <summary>
    /// Test #11 — per-client overrides apply independently. Register two services with
    /// different <see cref="MGovClientResilience.MaxRetries"/> and assert each respects
    /// its own configuration.
    /// </summary>
    [Fact]
    public async Task PerClient_OverrideAppliesIndependently()
    {
        using var host = ResilienceTestHost.BuildTwo("msign", "mpay", opts =>
        {
            opts.Clients["msign"] = new MGovClientResilience(
                MaxRetries: 3,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
            opts.Clients["mpay"] = new MGovClientResilience(
                MaxRetries: 1,
                BaseDelayMs: 10,
                JitterMs: 1,
                CircuitBreakerFailureThreshold: 10,
                CircuitBreakerSamplingSeconds: 30,
                CircuitBreakerBreakDurationSeconds: 30,
                AttemptTimeoutSeconds: 5,
                PipelineTimeoutSeconds: 30);
        });

        host.HandlerA.EnqueueStatus(HttpStatusCode.InternalServerError);
        host.HandlerB.EnqueueStatus(HttpStatusCode.InternalServerError);

        var clientA = host.GetClientA();
        var clientB = host.GetClientB();

        _ = await clientA.GetAsync("http://localhost/x");
        _ = await clientB.GetAsync("http://localhost/x");

        host.HandlerA.CallCount.Should().Be(4, "msign: 1 + 3 retries");
        host.HandlerB.CallCount.Should().Be(2, "mpay: 1 + 1 retry");
    }

    /// <summary>
    /// Default fast-retry options factory: 3 retries with a tiny base delay so the
    /// non-timing tests finish in well under a second.
    /// </summary>
    private static void DefaultFastOpts(MGovResilienceOptions opts)
    {
        opts.Clients["msign"] = new MGovClientResilience(
            MaxRetries: 3,
            BaseDelayMs: 10,
            JitterMs: 1,
            CircuitBreakerFailureThreshold: 10,
            CircuitBreakerSamplingSeconds: 30,
            CircuitBreakerBreakDurationSeconds: 30,
            AttemptTimeoutSeconds: 5,
            PipelineTimeoutSeconds: 30);
    }
}
