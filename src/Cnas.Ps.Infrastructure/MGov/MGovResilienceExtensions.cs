using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// DI extension that decorates an MGov typed <c>HttpClient</c> registration with a
/// resilience pipeline: outer total-timeout, retry with exponential backoff and jitter,
/// circuit breaker, and per-attempt timeout. Implements CLAUDE.md §6.2 ("retryable, 3x
/// exponential backoff, timeout-bounded") and TOR R0100 ("Polly retry / circuit breaker").
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline ordering (outermost → innermost).</b> Polly v8 executes strategies in
/// the order they are added. The HTTP-canonical order is:
/// <list type="number">
///   <item><description><b>Total Timeout</b> — hard wall-clock budget across the entire
///   pipeline. Cancels the call once the budget is spent, regardless of how many retries
///   remain.</description></item>
///   <item><description><b>Retry</b> — re-issues the request on transient failures
///   (5xx / 408 / 429 / <see cref="System.Net.Http.HttpRequestException"/> /
///   <see cref="TimeoutRejectedException"/>) with exponential backoff and ±jitter.
///   Honours <c>Retry-After</c> when the upstream provides one.</description></item>
///   <item><description><b>Circuit Breaker</b> — fast-fails once <c>FailureThreshold</c>
///   consecutive transient failures occur within the sampling window, holding the
///   circuit open for <c>BreakDurationSeconds</c> before admitting a half-open probe.
///   Sits between Retry and the per-attempt timeout so the breaker counts each
///   individual attempt as a failure, not the entire retry stack.</description></item>
///   <item><description><b>Per-Attempt Timeout</b> — innermost. Each individual request
///   has its own ceiling; a slow response triggers a <see cref="TimeoutRejectedException"/>
///   which the retry layer classifies as transient.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>What gets retried.</b> Per the v10
/// <see cref="HttpRetryStrategyOptions"/> defaults: HTTP 408 (Request Timeout), 429 (Too
/// Many Requests — backoff honours <c>Retry-After</c>), 500 and above, plus
/// <see cref="System.Net.Http.HttpRequestException"/> (DNS / connect / read errors) and
/// <see cref="TimeoutRejectedException"/> (per-attempt timeout). Retry does NOT touch
/// 4xx in general (400, 401, 403, 404, 422) — those are caller errors and retrying them
/// would just multiply the same broken request.
/// </para>
/// <para>
/// <b>What gets logged.</b> Every retry attempt logs at WARN with the service name,
/// attempt number, the delay before the next attempt, and the response status code or
/// exception type. Request / response bodies are NEVER logged — they may carry citizen
/// PII (CLAUDE.md §5.6). Circuit transitions (open / close) log at ERROR / INFO
/// respectively so an SRE grep for the keywords "circuit breaker opened" / "circuit
/// breaker closed" surfaces the full timeline of an outage.
/// </para>
/// <para>
/// <b>When disabled.</b> If <see cref="MGovResilienceOptions.Enabled"/> is <c>false</c>
/// the resilience handler is still registered (so the rest of the HTTP pipeline is
/// shape-stable across enabled / disabled runs) but executes a no-op pass-through —
/// every request goes straight to the inner primary handler. This exists for
/// integration tests that need exact request counts; production must keep the flag on.
/// </para>
/// </remarks>
public static class MGovResilienceExtensions
{
    /// <summary>
    /// Decorates the supplied <see cref="IHttpClientBuilder"/> with the MGov resilience
    /// pipeline (see the type-level remarks for the strategy order and retry policy).
    /// </summary>
    /// <param name="builder">
    /// The typed-<c>HttpClient</c> builder returned by <c>AddHttpClient&lt;T, TImpl&gt;</c>.
    /// Apply this AFTER <c>ConfigurePrimaryHttpMessageHandler</c> so the resilience
    /// handler wraps the mTLS-aware <see cref="System.Net.Http.SocketsHttpHandler"/>.
    /// </param>
    /// <param name="serviceName">
    /// Stable service key (same keys used by <see cref="MTlsOptions.Certificates"/> —
    /// e.g. <c>"msign"</c>, <c>"mpay"</c>, <c>"mconnect"</c>). The key looks up
    /// per-service overrides in <see cref="MGovResilienceOptions.Clients"/>; if no
    /// override is found the registration uses <see cref="MGovClientResilience"/>'s
    /// default constructor values. The key is also embedded in every log line emitted
    /// by the pipeline so ops dashboards can grep by service.
    /// </param>
    /// <returns>The same <paramref name="builder"/> for fluent chaining.</returns>
    public static IHttpClientBuilder AddMGovResilience(
        this IHttpClientBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name must be non-empty.", nameof(serviceName));
        }

        // Pipeline name is suffixed so each per-service registration has its own metrics
        // tag (see ResilienceHttpClientBuilderExtensions.AddResilienceHandler remarks —
        // the final pipeline name is <client-name>-<pipeline-name>).
        var pipelineName = $"{serviceName}-resilience";

        builder.AddResilienceHandler(pipelineName, (pipeline, context) =>
        {
            // Resolve options + logger from the strategy's service provider. The
            // ResilienceHandlerContext.ServiceProvider matches the root container —
            // the same one that AddHttpClient resolves from — so logger and options
            // share the singleton instance used elsewhere.
            var opts = context.ServiceProvider.GetRequiredService<IOptions<MGovResilienceOptions>>().Value;
            var loggerFactory = context.ServiceProvider.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger($"Cnas.Ps.MGov.Resilience.{serviceName}");

            var perClient = ResolvePerClient(opts, serviceName);

            if (!opts.Enabled)
            {
                // Escape hatch — no-op pipeline. Polly's empty ResiliencePipelineBuilder
                // configuration is a pass-through, so we deliberately add nothing here.
                logger?.LogDebug(
                    "MGov resilience pipeline for service={Service} is disabled — registering pass-through.",
                    serviceName);
                return;
            }

            // 1. Outer total timeout — hard wall-clock ceiling on the entire pipeline.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(perClient.PipelineTimeoutSeconds),
                Name = $"{serviceName}-total-timeout",
            });

            // 2. Retry with exponential backoff + jitter. The default ShouldHandle
            //    predicate (transient HTTP failures only) is exactly the policy
            //    CLAUDE.md §6.2 prescribes — 5xx + transient network — and explicitly
            //    EXCLUDES 4xx (caller errors). We do not override it.
            //
            //    Polly v8 validates MaxRetryAttempts >= 1, so when an operator wants to
            //    disable retry (MaxRetries=0) we skip the strategy entirely rather than
            //    inject a no-op that would trip validation.
            if (perClient.MaxRetries >= 1)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = perClient.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(perClient.BaseDelayMs),
                    ShouldRetryAfterHeader = true,
                    Name = $"{serviceName}-retry",
                    OnRetry = args =>
                    {
                        // Logged at WARN — every retry is a sign of upstream trouble
                        // but not yet an incident. ERROR is reserved for circuit-open
                        // events.
                        var statusCode = args.Outcome.Result?.StatusCode;
                        var exceptionType = args.Outcome.Exception?.GetType().Name;
                        logger?.LogWarning(
                            "MGov retry: service={Service} attempt={Attempt} delayMs={DelayMs} status={Status} exception={Exception}.",
                            serviceName,
                            args.AttemptNumber + 1,
                            (int)args.RetryDelay.TotalMilliseconds,
                            statusCode,
                            exceptionType);
                        return ValueTask.CompletedTask;
                    },
                });
            }

            // 3. Circuit breaker. FailureRatio=1.0 + MinimumThroughput=threshold means
            //    "open after exactly THIS many failures inside the sampling window" —
            //    the simplest semantics for ops. The breaker sits BETWEEN retry and the
            //    per-attempt timeout so each attempt counts toward the threshold (not
            //    each retry stack).
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = Math.Max(2, perClient.CircuitBreakerFailureThreshold),
                SamplingDuration = TimeSpan.FromSeconds(perClient.CircuitBreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(perClient.CircuitBreakerBreakDurationSeconds),
                Name = $"{serviceName}-circuit-breaker",
                OnOpened = args =>
                {
                    // Logged at ERROR — the circuit opening is an incident signal.
                    // SRE on-call should grep "MGov circuit opened" to find the root.
                    logger?.LogError(
                        "MGov circuit opened: service={Service} breakDurationSec={BreakDurationSec} — downstream is being treated as unavailable.",
                        serviceName,
                        (int)args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    // Logged at INFO — the breaker reseting means the downstream
                    // recovered and traffic resumes.
                    logger?.LogInformation(
                        "MGov circuit closed: service={Service} — downstream is healthy, normal traffic resumed.",
                        serviceName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    logger?.LogInformation(
                        "MGov circuit half-opened: service={Service} — probing downstream for recovery.",
                        serviceName);
                    return ValueTask.CompletedTask;
                },
            });

            // 4. Innermost per-attempt timeout. Each individual request attempt is
            //    bounded independently — a slow response on attempt N gets cancelled
            //    via TimeoutRejectedException, which the retry layer (now outside) sees
            //    as a transient failure and retries.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(perClient.AttemptTimeoutSeconds),
                Name = $"{serviceName}-attempt-timeout",
            });
        });

        return builder;
    }

    /// <summary>
    /// Resolves the per-service overrides from <see cref="MGovResilienceOptions.Clients"/>,
    /// falling back to <see cref="MGovClientResilience"/>'s default-constructed values
    /// when a service key is missing.
    /// </summary>
    /// <param name="opts">Snapshot of the bound options.</param>
    /// <param name="serviceName">Service key to look up.</param>
    /// <returns>The per-service resilience knobs that the pipeline should apply.</returns>
    private static MGovClientResilience ResolvePerClient(
        MGovResilienceOptions opts,
        string serviceName)
    {
        // Case-insensitive lookup — operators routinely vary casing in JSON / YAML.
        if (opts.Clients.TryGetValue(serviceName, out var perClient))
        {
            return perClient;
        }
        return new MGovClientResilience();
    }
}
