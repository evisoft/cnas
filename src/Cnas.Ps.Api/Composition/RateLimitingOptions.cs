using System.ComponentModel.DataAnnotations;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Strongly-typed configuration for the CNAS rate-limiting middleware. Bound from
/// <c>Cnas:RateLimiting</c> in app settings and validated at start-up via
/// <see cref="Microsoft.Extensions.DependencyInjection.OptionsBuilderExtensions.ValidateOnStart{TOptions}"/>.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md §5.3 — "Rate limiting on auth endpoints (5 req/min)" is enforced via the
/// <see cref="Anonymous"/> policy. The other policies extend the SEC 008 requirement to
/// the rest of the surface: authenticated traffic, server-to-server callbacks, and a
/// process-wide concurrency ceiling.
/// </para>
/// <para>
/// Every value can be tuned at runtime by ops via configuration without redeploy. The
/// values are read once at registration time (the limiter SDK builds its partition
/// state once during DI build-out — same constraint as
/// <see cref="ObservabilityOptions"/>), so live edits require a pod restart to take
/// effect.
/// </para>
/// <para>
/// <b>The IP-trust chain.</b> The <see cref="Anonymous"/> and <see cref="Callback"/>
/// policies partition on caller IP. The IP is resolved as follows:
/// <list type="number">
///   <item>If <see cref="TrustForwardedHeaders"/> is <c>true</c> (default), the
///         <b>last</b> entry in <c>X-Forwarded-For</c> is used — the gateway that
///         forwards the request to the API host is the only hop CNAS controls, so the
///         right-most token is the most trustworthy. Earlier tokens are caller-supplied
///         and easily spoofed.</item>
///   <item>If the header is absent or the value is malformed, <c>HttpContext.Connection.RemoteIpAddress</c>
///         is used.</item>
///   <item>If neither is available the limiter falls back to a single shared "unknown"
///         partition — by design, so spoofed nullable IPs cannot escape throttling by
///         pretending to be unique callers.</item>
/// </list>
/// In production the rate-limiter MUST sit behind a hardened reverse proxy
/// (MCloud gateway / Traefik) that strips client-supplied <c>X-Forwarded-For</c>
/// values and replaces them with the gateway's own.
/// </para>
/// </remarks>
public sealed class RateLimitingOptions
{
    /// <summary>
    /// Configuration section name. Bind via
    /// <c>configuration.GetSection(RateLimitingOptions.SectionName)</c>.
    /// </summary>
    public const string SectionName = "Cnas:RateLimiting";

    /// <summary>
    /// Master switch. When <c>false</c> the limiter is registered as a permissive no-op
    /// so the middleware pipeline still composes (<c>UseRateLimiter</c> would otherwise
    /// throw on an empty policy set) but every request is admitted regardless of
    /// partition or burst rate. Use only in dev/test environments and for the brief
    /// emergency-flip window after a misconfiguration — production must stay
    /// <c>true</c>. Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether to honour the <c>X-Forwarded-For</c> header when partitioning anonymous
    /// traffic by IP. Defaults to <c>true</c>. Set to <c>false</c> only when the API is
    /// exposed directly to the public Internet without a reverse proxy — in that mode
    /// the connection's remote IP is authoritative and trusting a client-supplied
    /// header would let attackers rotate partitions trivially.
    /// </summary>
    public bool TrustForwardedHeaders { get; init; }

    /// <summary>Anonymous-traffic policy. See <see cref="RateLimitingPolicies.Anonymous"/>.</summary>
    public PolicyOptions Anonymous { get; init; } = new(PermitLimit: 5, WindowSeconds: 60, QueueLimit: 0);

    /// <summary>MGov callback policy. See <see cref="RateLimitingPolicies.Callback"/>.</summary>
    public PolicyOptions Callback { get; init; } = new(PermitLimit: 60, WindowSeconds: 60, QueueLimit: 0);

    /// <summary>Upload policy. See <see cref="RateLimitingPolicies.Upload"/>.</summary>
    public PolicyOptions Upload { get; init; } = new(PermitLimit: 10, WindowSeconds: 60, QueueLimit: 2);

    /// <summary>Authenticated default policy. See <see cref="RateLimitingPolicies.Authenticated"/>.</summary>
    public PolicyOptions Authenticated { get; init; } = new(PermitLimit: 200, WindowSeconds: 60, QueueLimit: 10);

    /// <summary>
    /// Number of concurrent in-flight requests the process will admit before queuing
    /// further requests on the global <c>ConcurrencyLimiter</c>. Defaults to 500 —
    /// generous enough that the partitioned policies above are the dominant gate under
    /// normal load, but a hard ceiling for the runaway case. Tune to match the pod's
    /// CPU/memory budget; too high and a hot CPU starts queueing into thread-pool
    /// exhaustion before the limiter notices.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int GlobalConcurrencyLimit { get; init; } = 500;

    /// <summary>
    /// Number of additional requests the global concurrency limiter will queue before
    /// rejecting. Defaults to 1000. Once queue + active = limit + queue-limit, new
    /// requests get 429 immediately rather than waiting for an indefinite slot.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int GlobalConcurrencyQueueLimit { get; init; } = 1000;

    /// <summary>
    /// Configuration record for a single fixed/sliding-window policy. All three
    /// values are validated at start-up: each must be a non-negative integer, the
    /// permit limit must be ≥ 1, and the window must be ≥ 1 second.
    /// </summary>
    /// <param name="PermitLimit">
    /// Maximum number of requests admitted per partition per window. Must be ≥ 1.
    /// </param>
    /// <param name="WindowSeconds">
    /// Duration of the rate-limit window in seconds. Must be ≥ 1. The
    /// <c>Authenticated</c> policy further subdivides this window into segments
    /// internally for sliding-window behaviour.
    /// </param>
    /// <param name="QueueLimit">
    /// Maximum number of waiting requests held while the partition is at the permit
    /// limit. <c>0</c> = reject immediately (correct for IP-partitioned policies where
    /// queueing across un-related callers would inflate p99 latency for unrelated
    /// traffic). Must be ≥ 0.
    /// </param>
    public sealed record PolicyOptions(
        [property: Range(1, int.MaxValue)] int PermitLimit,
        [property: Range(1, int.MaxValue)] int WindowSeconds,
        [property: Range(0, int.MaxValue)] int QueueLimit);
}
