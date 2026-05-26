namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Stable string identifiers for the rate-limiting policies registered by
/// <see cref="ApiCompositionRoot.AddCnasApi"/>. Controllers reference these constants in
/// <c>[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting]</c> attributes instead of
/// inlining string literals — the policy names are part of the registration contract and
/// drift between attribute strings and registered policies produces a silent 404 on the
/// limiter lookup (the limiter would log a warning but every request would pass through
/// unthrottled).
/// </summary>
/// <remarks>
/// <para>
/// Policy ladder (low → high request budget):
/// <list type="bullet">
///   <item><see cref="Anonymous"/> — public, IP-partitioned, very low budget.</item>
///   <item><see cref="Upload"/> — authenticated, user-partitioned, low budget (bytes are expensive).</item>
///   <item><see cref="Callback"/> — MGov server-to-server callbacks, IP-partitioned, medium budget.</item>
///   <item><see cref="Authenticated"/> — default for authenticated traffic, user-partitioned, high budget.</item>
/// </list>
/// </para>
/// <para>
/// All four policies are layered behind a process-wide
/// <c>ConcurrencyLimiter</c> registered as
/// <see cref="Microsoft.AspNetCore.RateLimiting.RateLimiterOptions.GlobalLimiter"/> — defence
/// in depth against a runaway client that somehow side-steps a partitioned bucket.
/// </para>
/// <para>
/// CLAUDE.md §5.3 — "Rate limiting on auth endpoints (5 req/min)" is implemented by
/// <see cref="Anonymous"/>; the broader policy table is documented on
/// <see cref="RateLimitingOptions"/>.
/// </para>
/// </remarks>
public static class RateLimitingPolicies
{
    /// <summary>
    /// Throttles anonymous (un-authenticated) traffic. Partition key is the caller's IP
    /// address (with <c>X-Forwarded-For</c> handling — see <see cref="RateLimitingOptions"/>
    /// for the trust-chain rules). Default: 5 requests / 60 seconds, queue 0.
    /// Applied to <c>PublicController</c>, <c>MPassSamlController</c>, and any other
    /// <c>[AllowAnonymous]</c> endpoint.
    /// </summary>
    public const string Anonymous = "Anonymous";

    /// <summary>
    /// Throttles inbound MGov server-to-server callbacks (MSign, MPay). Partition key is
    /// the caller's IP. Default: 60 requests / 60 seconds, queue 0 — callbacks must not
    /// pile up because a queued retry from MGov looks identical to a fresh one and will
    /// surface as a duplicate webhook delivery. The bucket is larger than
    /// <see cref="Anonymous"/> because MGov bursts (re-deliveries after upstream
    /// recovery) are legitimate, expected traffic and must not get 429'd.
    /// </summary>
    public const string Callback = "Callback";

    /// <summary>
    /// Throttles authenticated uploads. Partition key is the caller's user id (sub claim
    /// or name identifier) — falls back to IP when the principal somehow lacks both
    /// (which should not happen on an <c>[Authorize]</c> endpoint; the limiter logs a
    /// warning if it does). Default: 10 requests / 60 seconds, queue 2. Stricter than
    /// <see cref="Authenticated"/> because each upload allocates a real DB row, MinIO
    /// PUT, and (in many cases) magic-byte scanning bandwidth.
    /// </summary>
    public const string Upload = "Upload";

    /// <summary>
    /// Default policy for authenticated REST traffic. Partition key is the caller's user
    /// id (not the IP — citizens commonly share NATs and a corporate NAT pool would
    /// otherwise collapse hundreds of legitimate users into one bucket). Default: 200
    /// requests / 60 seconds with 4 sliding-window segments, queue 10. Applied as a
    /// controller-level attribute on every <c>[Authorize]</c> controller.
    /// </summary>
    public const string Authenticated = "Authenticated";
}
