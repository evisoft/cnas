using System.Collections.Generic;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Configuration root for the MGov resilience pipeline. Bound from
/// <c>Cnas:MGov:Resilience</c>. Backs <see cref="MGovResilienceExtensions.AddMGovResilience"/>,
/// which decorates every MGov typed-<c>HttpClient</c> registration with a Polly v8
/// pipeline (retry + circuit breaker + per-attempt &amp; total timeouts).
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md §6.2 mandates "retryable, 3x exponential backoff, timeout-bounded" for any
/// outbound side-effect that may transiently fail; TOR R0100 makes the same demand of the
/// MGov suite (REST + SOAP profiles). This options class is the single source of truth
/// for the knobs.
/// </para>
/// <para>
/// Each entry in <see cref="Clients"/> overrides the per-service defaults using the same
/// stable service names that key <see cref="MTlsOptions.Certificates"/> — e.g. <c>"msign"</c>,
/// <c>"mpay"</c>, <c>"mconnect"</c>, <c>"mnotify"</c>, <c>"mlog"</c>, <c>"mdocs"</c>,
/// <c>"mconnect-events"</c>, <c>"mcabinet"</c>. When a service is missing from the
/// dictionary the registration falls back to <see cref="MGovClientResilience"/>'s default
/// constructor values, which themselves implement CLAUDE.md §6.2's defaults.
/// </para>
/// <para>
/// <b>Escape hatch.</b> When <see cref="Enabled"/> is <c>false</c> the pipeline is
/// registered as a no-op so integration tests that exercise raw <c>HttpClient</c>
/// behaviour (e.g. exact request count, exact wire bytes) can opt out cleanly. This
/// mirrors the <c>RateLimitingOptions.Enabled</c> kill-switch in the API layer —
/// production environments must always leave this <c>true</c>; the flag exists for
/// test isolation and for the brief emergency-flip window after a pipeline
/// misconfiguration.
/// </para>
/// </remarks>
public sealed class MGovResilienceOptions
{
    /// <summary>
    /// Configuration section name. Bind via
    /// <c>configuration.GetSection(MGovResilienceOptions.SectionName)</c>.
    /// </summary>
    public const string SectionName = "Cnas:MGov:Resilience";

    /// <summary>
    /// Master switch. When <c>false</c>, <see cref="MGovResilienceExtensions.AddMGovResilience"/>
    /// registers a no-op resilience handler (pass-through) so requests flow directly to
    /// the inner primary handler. The default is <c>true</c>; production environments
    /// must keep it enabled. See the type-level remarks for the rationale.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Per-service overrides keyed by stable service name (case-insensitive). A missing
    /// entry resolves to <see cref="MGovClientResilience"/>'s default constructor values.
    /// The keys match those used by <see cref="MTlsOptions.Certificates"/> so operators
    /// can correlate the two configuration trees by service name.
    /// </summary>
    public Dictionary<string, MGovClientResilience> Clients { get; init; }
        = new(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-service resilience knobs. The defaults implement CLAUDE.md §6.2 — 3 retries with
/// exponential backoff (200 ms / 400 ms / 800 ms median, ±100 ms jitter), 5-failure
/// circuit breaker holding 30 s, 10 s per-attempt timeout, 60 s total pipeline timeout.
/// </summary>
/// <param name="MaxRetries">
/// Maximum retry attempts AFTER the initial request. Setting this to <c>3</c> produces
/// up to 4 total invocations (initial + 3 retries). Setting it to <c>0</c> disables retry
/// while leaving the rest of the pipeline (circuit breaker, timeout) intact.
/// </param>
/// <param name="BaseDelayMs">
/// Median delay before the first retry, in milliseconds. Subsequent retries scale
/// exponentially (<c>BaseDelayMs</c> · 2^(attempt − 1)), so 200 ms produces ~200, ~400,
/// ~800 ms before retries 1, 2, 3 respectively. Jitter (see <paramref name="JitterMs"/>)
/// randomises ±this fraction of the computed delay to prevent thundering-herd retries.
/// </param>
/// <param name="JitterMs">
/// Maximum absolute jitter applied to each retry delay. Currently informational — Polly
/// v8's <c>UseJitter</c> flag computes jitter proportionally to the base delay rather
/// than from a fixed millisecond budget, so this value is preserved for documentation
/// and forward-compatibility with a future custom <c>DelayGenerator</c>.
/// </param>
/// <param name="CircuitBreakerFailureThreshold">
/// Number of consecutive failures within <see cref="CircuitBreakerSamplingSeconds"/>
/// that opens the circuit. Combined with a 1.0 failure ratio this becomes "this many
/// consecutive failures trips the breaker" — the simplest semantics for an operations
/// team.
/// </param>
/// <param name="CircuitBreakerSamplingSeconds">
/// Sliding-window length over which <see cref="CircuitBreakerFailureThreshold"/> is
/// evaluated. Defaults to 30 s — long enough to see a real outage develop but short
/// enough that the breaker auto-resets within a reasonable on-call cycle.
/// </param>
/// <param name="CircuitBreakerBreakDurationSeconds">
/// Wall-clock duration the breaker stays open before transitioning to half-open and
/// admitting a probe request. 30 s by default — short enough that a recovered
/// downstream sees traffic resume quickly, long enough that flapping under load
/// doesn't hammer the dependency.
/// </param>
/// <param name="AttemptTimeoutSeconds">
/// Per-attempt timeout. Each retry attempt that exceeds this duration is cancelled and
/// counted as a transient failure (Polly v8 raises <c>TimeoutRejectedException</c>,
/// which the default retry predicate treats as transient). 10 s by default.
/// </param>
/// <param name="PipelineTimeoutSeconds">
/// Total wall-clock budget for the entire pipeline (initial attempt + retries +
/// backoff). When exceeded the caller observes a <c>TimeoutRejectedException</c>
/// even if a retry was in flight. 60 s by default — must be large enough to
/// accommodate <c>MaxRetries</c> · <c>AttemptTimeoutSeconds</c> plus all backoff
/// delays; otherwise the inner retries get cut short by the outer timeout.
/// </param>
public sealed record MGovClientResilience(
    int MaxRetries = 3,
    int BaseDelayMs = 200,
    int JitterMs = 100,
    int CircuitBreakerFailureThreshold = 5,
    int CircuitBreakerSamplingSeconds = 30,
    int CircuitBreakerBreakDurationSeconds = 30,
    int AttemptTimeoutSeconds = 10,
    int PipelineTimeoutSeconds = 60);
