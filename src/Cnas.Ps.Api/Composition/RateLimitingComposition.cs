using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// DI composition for the CNAS rate-limiting middleware. Wires
/// <see cref="RateLimitingOptions"/> from configuration, registers four named partition
/// policies (<see cref="RateLimitingPolicies"/>) plus a global concurrency ceiling, and
/// configures the rejected-request response per CLAUDE.md §5.3.
/// </summary>
public static class RateLimitingComposition
{
    /// <summary>
    /// JSON content type for ProblemDetails responses. Stored as a constant so the
    /// rejected-request handler doesn't allocate a new string per 429.
    /// </summary>
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// RFC 6585 §4 — the canonical "Too Many Requests" type URI. Embedded in every
    /// 429 ProblemDetails body so external clients can branch on the type without
    /// parsing the title or detail strings.
    /// </summary>
    private const string RateLimitedTypeUri = "https://tools.ietf.org/html/rfc6585#section-4";

    /// <summary>
    /// Partition key used when the caller's IP cannot be resolved. A single shared
    /// partition (rather than per-request unique keys) so a caller spoofing nullable
    /// X-Forwarded-For values cannot escape throttling by appearing to be many
    /// distinct hosts.
    /// </summary>
    private const string UnknownIpPartitionKey = "ip:unknown";

    /// <summary>
    /// Partition-key prefix for authenticated principals. Combined with the user's
    /// stable identifier (subject claim) it becomes the limiter's partition key.
    /// </summary>
    private const string UserPartitionPrefix = "user:";

    /// <summary>
    /// Partition-key prefix for un-authenticated callers identified by IP.
    /// </summary>
    private const string IpPartitionPrefix = "ip:";

    /// <summary>
    /// JSON serializer options used for the 429 ProblemDetails body. Camel-case
    /// property names match the rest of the API surface.
    /// </summary>
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers <see cref="RateLimitingOptions"/> + the four named partition policies
    /// + the global concurrency limiter on the supplied service collection. Idempotent:
    /// calling twice is a no-op (the second registration is dropped by the framework).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="RateLimitingOptions.Enabled"/> is <c>false</c> every policy is
    /// replaced with a "no-limiter" partition that admits every request. The middleware
    /// pipeline must always include <c>UseRateLimiter</c> (it does — see
    /// <see cref="ApiCompositionRoot.UseCnasApiPipeline"/>); registering at least one
    /// permissive policy under the same name keeps <c>[EnableRateLimiting]</c>
    /// attributes resolving cleanly.
    /// </para>
    /// <para>
    /// Options validation: negative limits, zero windows, etc. trip
    /// <see cref="OptionsBuilderDataAnnotationsExtensions.ValidateDataAnnotations{TOptions}"/>
    /// + <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}"/>, surfacing as
    /// an <see cref="OptionsValidationException"/> during host build — same fail-fast
    /// pattern as the rest of the composition root.
    /// </para>
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Host configuration.</param>
    /// <returns><paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddCnasRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Snapshot the rate-limit options once at registration time, reading directly
        // from IConfiguration. The OTel SDK / DI graph would otherwise force us to call
        // services.BuildServiceProvider() inside the AddRateLimiter callback, which
        // constructs a PARALLEL root container (CLAUDE.md anti-pattern — duplicates
        // singletons, leaks scopes, breaks options change-token rebinding). The limiter
        // wires its partitions during DI build-out and cannot honour live edits anyway,
        // so the configuration snapshot taken here is functionally equivalent without
        // the parallel-container hazard. Validation still runs via the AddOptions chain
        // below (ValidateOnStart), so a malformed configuration fails the host build
        // just as before.
        var opts = configuration.GetSection(RateLimitingOptions.SectionName)
            .Get<RateLimitingOptions>() ?? new RateLimitingOptions();

        // Bind options + validate at start-up. Per-property DataAnnotations (Range etc.)
        // cover the "negative permit limit" case the acceptance criteria require.
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(static o =>
                o.Anonymous.PermitLimit >= 1
                && o.Anonymous.WindowSeconds >= 1
                && o.Callback.PermitLimit >= 1
                && o.Callback.WindowSeconds >= 1
                && o.Upload.PermitLimit >= 1
                && o.Upload.WindowSeconds >= 1
                && o.Authenticated.PermitLimit >= 1
                && o.Authenticated.WindowSeconds >= 1,
                "Rate-limiting permit limits must be >= 1 and window sizes must be >= 1 second.")
            .ValidateOnStart();

        services.AddRateLimiter(limiterOptions =>
        {
            ConfigureRejectedResponse(limiterOptions);
            ConfigureGlobalLimiter(limiterOptions, opts);

            limiterOptions.AddPolicy(RateLimitingPolicies.Anonymous, ctx =>
                opts.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ResolveIpPartitionKey(ctx, opts.TrustForwardedHeaders),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = opts.Anonymous.PermitLimit,
                            Window = TimeSpan.FromSeconds(opts.Anonymous.WindowSeconds),
                            QueueLimit = opts.Anonymous.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        })
                    : NoLimitPartition(RateLimitingPolicies.Anonymous));

            limiterOptions.AddPolicy(RateLimitingPolicies.Callback, ctx =>
                opts.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ResolveIpPartitionKey(ctx, opts.TrustForwardedHeaders),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = opts.Callback.PermitLimit,
                            Window = TimeSpan.FromSeconds(opts.Callback.WindowSeconds),
                            QueueLimit = opts.Callback.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        })
                    : NoLimitPartition(RateLimitingPolicies.Callback));

            limiterOptions.AddPolicy(RateLimitingPolicies.Upload, ctx =>
                opts.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ResolveUserPartitionKey(ctx, opts.TrustForwardedHeaders),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = opts.Upload.PermitLimit,
                            Window = TimeSpan.FromSeconds(opts.Upload.WindowSeconds),
                            QueueLimit = opts.Upload.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        })
                    : NoLimitPartition(RateLimitingPolicies.Upload));

            limiterOptions.AddPolicy(RateLimitingPolicies.Authenticated, ctx =>
                opts.Enabled
                    ? RateLimitPartition.GetSlidingWindowLimiter(
                        partitionKey: ResolveUserPartitionKey(ctx, opts.TrustForwardedHeaders),
                        factory: _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = opts.Authenticated.PermitLimit,
                            Window = TimeSpan.FromSeconds(opts.Authenticated.WindowSeconds),
                            SegmentsPerWindow = 4,
                            QueueLimit = opts.Authenticated.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        })
                    : NoLimitPartition(RateLimitingPolicies.Authenticated));
        });

        return services;
    }

    /// <summary>
    /// Configures the limiter's process-wide concurrency ceiling. Independent of the
    /// partitioned policies — defends against the runaway-client case where one
    /// principal somehow side-steps a partitioned bucket (e.g. by rotating tokens or
    /// IPs faster than the limiter can detect).
    /// </summary>
    /// <param name="limiterOptions">Limiter configuration being built.</param>
    /// <param name="opts">Validated CNAS options snapshot.</param>
    private static void ConfigureGlobalLimiter(RateLimiterOptions limiterOptions, RateLimitingOptions opts)
    {
        limiterOptions.GlobalLimiter = opts.Enabled
            ? PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("global", _ =>
                    new ConcurrencyLimiterOptions
                    {
                        PermitLimit = opts.GlobalConcurrencyLimit,
                        QueueLimit = opts.GlobalConcurrencyQueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }))
            : PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetNoLimiter("global"));
    }

    /// <summary>
    /// Wires the 429-response shape: status 429, <c>Retry-After</c> header carrying the
    /// next-window-in-seconds, ProblemDetails JSON body with the
    /// <see cref="RateLimitedTypeUri"/> type. Policy name is exposed as an extension
    /// field so clients can branch on which bucket they hit.
    /// </summary>
    /// <param name="limiterOptions">Limiter configuration being built.</param>
    private static void ConfigureRejectedResponse(RateLimiterOptions limiterOptions)
    {
        limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        limiterOptions.OnRejected = (context, cancellationToken) =>
        {
            var http = context.HttpContext;

            // Best-effort policy attribution. ASP.NET's RateLimitingMetadata is the
            // canonical accessor; when the rejection comes from the global limiter the
            // policy metadata is absent and we fall back to "global".
            var policyName = http.GetEndpoint()?.Metadata
                .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                ?? "global";

            // Retry-After. The lease metadata may carry a TimeSpan hint; otherwise
            // fall back to 1 second (cannot be 0 — that's a violation of HTTP/1.1).
            int retryAfterSeconds = 1;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                if (retryAfterSeconds < 1)
                {
                    retryAfterSeconds = 1;
                }
            }

            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers[HeaderNames.RetryAfter] =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            http.Response.ContentType = ProblemJsonContentType;

            var problem = new ProblemDetails
            {
                Type = RateLimitedTypeUri,
                Title = ErrorCodes.RateLimited,
                Status = StatusCodes.Status429TooManyRequests,
                Detail = $"Rate limit exceeded for policy '{policyName}'. Retry after {retryAfterSeconds}s.",
            };
            problem.Extensions["policy"] = policyName;
            problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;
            problem.Extensions["errorCode"] = ErrorCodes.RateLimited;

            // Log at DEBUG (not INFO) — partition keys carry user ids / IPs which are
            // PII per CLAUDE.md §5.6. SREs needing a count use the OTel meter
            // instrumented elsewhere; we never put raw partition keys into INFO logs.
            var logger = http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("Cnas.Ps.Api.RateLimiting");
            logger?.LogDebug(
                "Rate-limit rejection policy={Policy} retryAfterSeconds={RetryAfterSeconds} path={Path}.",
                policyName,
                retryAfterSeconds,
                http.Request.Path.Value);

            return new ValueTask(JsonSerializer.SerializeAsync(
                http.Response.Body, problem, ProblemJsonOptions, cancellationToken));
        };
    }

    /// <summary>
    /// Resolves the partition key for an IP-bucketed policy. Falls back to
    /// <see cref="UnknownIpPartitionKey"/> when the IP cannot be determined.
    /// </summary>
    /// <param name="ctx">Incoming HTTP context.</param>
    /// <param name="trustForwardedHeaders">
    /// When <c>true</c>, the right-most token of <c>X-Forwarded-For</c> is used; when
    /// <c>false</c>, only the connection's remote IP is consulted.
    /// </param>
    /// <returns>Stable string key suitable for use as a partition identifier.</returns>
    private static string ResolveIpPartitionKey(HttpContext ctx, bool trustForwardedHeaders)
    {
        if (trustForwardedHeaders
            && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff)
            && xff.Count > 0
            && !string.IsNullOrWhiteSpace(xff[^1]))
        {
            // Right-most token. The XFF header is a comma-joined list; the right-most
            // hop is the gateway closest to us and the only one we control. Earlier
            // tokens may be caller-supplied (and easily spoofed).
            var raw = xff[^1]!;
            var lastComma = raw.LastIndexOf(',');
            var token = (lastComma >= 0 ? raw[(lastComma + 1)..] : raw).Trim();
            if (!string.IsNullOrEmpty(token))
            {
                return IpPartitionPrefix + token;
            }
        }

        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return UnknownIpPartitionKey;
        }

        // Normalise IPv4-mapped IPv6 to its IPv4 form so 192.0.2.1 and ::ffff:192.0.2.1
        // share a single partition — different transports through Kestrel can produce
        // either form for the same caller.
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }
        return IpPartitionPrefix + remote.ToString();
    }

    /// <summary>
    /// Resolves the partition key for a user-bucketed policy. Prefers the principal's
    /// stable subject claim; falls back to the IP key when the principal lacks an
    /// identifier (which should never happen on an <c>[Authorize]</c> endpoint — the
    /// caller logs a warning if it does).
    /// </summary>
    /// <param name="ctx">Incoming HTTP context.</param>
    /// <param name="trustForwardedHeaders">Whether to honour <c>X-Forwarded-For</c> for the fallback path.</param>
    /// <returns>Stable string key suitable for use as a partition identifier.</returns>
    private static string ResolveUserPartitionKey(HttpContext ctx, bool trustForwardedHeaders)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? user.FindFirst("sub")?.Value
                      ?? user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(sub))
            {
                return UserPartitionPrefix + sub;
            }
        }

        var logger = ctx.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Cnas.Ps.Api.RateLimiting");
        logger?.LogWarning(
            "User-partitioned rate-limit policy applied to a request without an authenticated principal at {Path} — falling back to IP partition.",
            ctx.Request.Path.Value);

        return ResolveIpPartitionKey(ctx, trustForwardedHeaders);
    }

    /// <summary>
    /// Builds a permissive, no-op partition used when <see cref="RateLimitingOptions.Enabled"/>
    /// is <c>false</c>. The partition key is the policy name so each disabled policy
    /// has its own slot in the limiter's lookup, but no actual throttling occurs.
    /// </summary>
    /// <param name="policyName">Policy name (used as the partition key).</param>
    /// <returns>A no-limiter partition that admits every request.</returns>
    private static RateLimitPartition<string> NoLimitPartition(string policyName)
        => RateLimitPartition.GetNoLimiter(policyName);
}
