namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Configuration for the Cloudflare Turnstile CAPTCHA verifier (R0035 — abuse-prevention
/// gateway on the anonymous UC01 / UC02 public surface). Bound from the
/// <c>Cnas:Captcha:Turnstile</c> configuration section per CLAUDE.md §1.8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> <see cref="SecretKey"/> is sensitive and MUST come from the
/// secrets manager (Vault / k8s Secret / MCloud KMS) in staging and production —
/// never from <c>appsettings.json</c>. <see cref="SiteKey"/> is the public widget id
/// that the SPA embeds; it is safe to ship in environment config and surface to the
/// client.
/// </para>
/// <para>
/// <b>Stable configuration.</b> Rotating <see cref="SecretKey"/> requires a coordinated
/// Cloudflare-dashboard rotation — the verifier picks up the new value the next time
/// DI re-creates the singleton, but in-flight requests use the old value until they
/// complete. Operators rotating a Turnstile secret should drain anonymous traffic
/// (or accept short-lived 400/503s) before swapping.
/// </para>
/// </remarks>
public sealed class TurnstileOptions
{
    /// <summary>
    /// Fully qualified configuration section name. Composes under the shared
    /// <c>Cnas</c> root so it sits alongside the rest of the project configuration
    /// (e.g. <c>Cnas:RateLimiting</c>, <c>Cnas:FieldEncryption</c>).
    /// </summary>
    public const string SectionName = "Cnas:Captcha:Turnstile";

    /// <summary>
    /// Server-side secret key issued by Cloudflare. Required unless
    /// <see cref="BypassForTesting"/> is <c>true</c>. NEVER commit this value — it
    /// is sourced from the secrets manager (CLAUDE.md §1.8).
    /// </summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Client-side site key surfaced to the SPA so the Turnstile widget can render.
    /// Safe to embed in environment config and to surface via the public configuration
    /// endpoint. Required unless <see cref="BypassForTesting"/> is <c>true</c>.
    /// </summary>
    public string SiteKey { get; init; } = string.Empty;

    /// <summary>
    /// Verification URL. Defaults to Cloudflare's standard endpoint
    /// (<c>https://challenges.cloudflare.com/turnstile/v0/siteverify</c>). Override
    /// only for testing against a controlled mock — the production endpoint is the
    /// only Cloudflare-blessed surface.
    /// </summary>
    public string VerifyUrl { get; init; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>
    /// Per-call HTTP timeout. Default 4 seconds — CAPTCHA providers respond fast
    /// (typical latency &lt; 200 ms); a generous-but-bounded budget guards against
    /// the gateway becoming a DoS amplifier when Cloudflare is degraded.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// When <c>true</c>, the verifier is a no-op (always returns success) without
    /// making the HTTP call. Used by integration / E2E fixtures so the suite never
    /// hits Cloudflare from CI. Production / staging config MUST set this to
    /// <c>false</c> — leaving it <c>true</c> on a public-facing deployment removes
    /// the abuse guard entirely.
    /// </summary>
    public bool BypassForTesting { get; init; }
}
