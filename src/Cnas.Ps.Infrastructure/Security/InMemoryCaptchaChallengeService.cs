using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// R0507 / TOR CF 01.10 — in-memory <see cref="ICaptchaChallengeService"/>
/// implementation. Suitable for single-pod deployments and the test suite;
/// multi-pod production deployments should swap this for a Redis-backed
/// implementation so a challenge issued on pod A can be verified on pod B.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage.</b> A
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the opaque
/// token, valued by the (answer, expiresAtUtc, verifiedAtUtc, isConsumed)
/// tuple. Expired and consumed entries are evicted lazily on access — the
/// dictionary stays bounded under realistic traffic and a low-fixed sweep
/// runs on every miss to prevent unbounded growth in a quiet system that
/// only sees expired keys.
/// </para>
/// <para>
/// <b>Image format.</b> The default render path ships an SVG so the service
/// has no dependency on <c>System.Drawing.Common</c> (which requires
/// <c>libgdiplus</c> on Linux CI runners and is brittle). The SVG is small
/// (a few hundred bytes), renders identically in every modern browser, and
/// is intentionally NOT obfuscated beyond a per-character offset rotation —
/// the underlying token store is the security primitive, not the image
/// difficulty. Sophisticated bots will solve any visible CAPTCHA, so the
/// goal here is to add cost to drive-by abuse, not to defeat a determined
/// attacker.
/// </para>
/// <para>
/// <b>Lifetime.</b> Singleton. Token rows live in a process-static
/// dictionary so they survive across scoped service-provider activations.
/// </para>
/// </remarks>
public sealed class InMemoryCaptchaChallengeService : ICaptchaChallengeService
{
    /// <summary>Characters used in the random challenge code.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Length of the generated challenge code in characters.</summary>
    public const int CodeLength = 6;

    /// <summary>Default time-to-live for an issued challenge token.</summary>
    public static readonly TimeSpan DefaultTokenTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Grace window during which a successfully verified token is honoured by
    /// the downstream gate (e.g. PublicCatalogController) on the
    /// <c>X-Captcha-Token</c> header.
    /// </summary>
    public static readonly TimeSpan DefaultPostVerifyWindow = TimeSpan.FromMinutes(10);

    /// <summary>MIME type returned by the default SVG renderer.</summary>
    public const string SvgMimeType = "image/svg+xml";

    private readonly ICnasTimeProvider _clock;
    private readonly ConcurrentDictionary<string, ChallengeEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Constructs the service with a clock abstraction.</summary>
    /// <param name="clock">UTC clock used for issue / verify timestamps.</param>
    public InMemoryCaptchaChallengeService(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<CaptchaIssueDto> IssueAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        // Lazy sweep so the dictionary never grows unbounded in low-traffic
        // environments. Tied to every Issue call because that's the natural
        // rate-of-cleanup pacing.
        SweepExpired(now);

        var token = NewToken();
        var code = NewCode();
        var entry = new ChallengeEntry(
            Answer: code,
            ExpiresAtUtc: now + DefaultTokenTtl,
            VerifiedAtUtc: null,
            IsConsumed: false);
        // ConcurrentDictionary.TryAdd is theoretically lossy if two tokens
        // collide — but the cryptographic generator gives us a >2^128 keyspace
        // so the chance is negligible. Defensive return uses TryAdd anyway.
        _entries.TryAdd(token, entry);

        var svgBytes = Encoding.UTF8.GetBytes(BuildSvg(code));
        var imageBase64 = Convert.ToBase64String(svgBytes);
        return Task.FromResult(new CaptchaIssueDto(
            ChallengeToken: token,
            ImageBase64: imageBase64,
            MimeType: SvgMimeType));
    }

    /// <inheritdoc />
    public Task<Result> VerifyAsync(string? challengeToken, string? answer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(challengeToken) || string.IsNullOrWhiteSpace(answer))
        {
            return Task.FromResult(Result.Failure(
                ErrorCodes.CaptchaTokenMissing, "Challenge token and answer are required."));
        }

        // CAS-loop on the entry so two concurrent verifies for the SAME token
        // cannot both pass the IsConsumed/VerifiedAtUtc gates and both stamp
        // success — only the single TryUpdate winner observes the un-verified
        // entry and writes the verified-stamp transition. Bounded loop iters
        // because the only legitimate state transition is a single one-shot
        // VerifiedAtUtc null→now flip, so a contended retry sees the already-
        // verified entry on the next read and short-circuits.
        var now = _clock.UtcNow;
        while (true)
        {
            if (!_entries.TryGetValue(challengeToken, out var entry))
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Unknown or already-consumed challenge."));
            }

            if (entry.IsConsumed)
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Challenge has already been consumed."));
            }
            if (entry.ExpiresAtUtc <= now)
            {
                // Evict + fail. A request that arrives right at the TTL boundary
                // is treated as expired so the user experience is uniform with a
                // late retry.
                _entries.TryRemove(challengeToken, out _);
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Challenge has expired."));
            }
            if (!string.Equals(entry.Answer, answer.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Incorrect answer."));
            }
            // Already verified by a prior caller — racing concurrent verifies
            // resolve here: the SECOND winner observes the freshly-stamped
            // entry and reports invalid (one-shot success contract).
            if (entry.VerifiedAtUtc.HasValue)
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Challenge has already been verified."));
            }

            // Atomically transition the entry from un-verified to verified.
            // The entry is NOT immediately consumed because the downstream
            // gate (PublicCatalogController) needs to honour the X-Captcha-
            // Token for the brief post-verify window. IsConsumed is flipped
            // by a separate ConsumeAsync call from the gate.
            var updated = entry with { VerifiedAtUtc = now };
            if (_entries.TryUpdate(challengeToken, updated, entry))
            {
                return Task.FromResult(Result.Success());
            }
            // Lost the CAS race — another thread mutated the entry. Reload
            // and re-evaluate. The loop is bounded because the only legal
            // mutations (verify-stamp / consume) both short-circuit on the
            // next iteration.
        }
    }

    /// <inheritdoc />
    public Task<bool> IsRecentlyVerifiedAsync(string? challengeToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(challengeToken))
        {
            return Task.FromResult(false);
        }
        if (!_entries.TryGetValue(challengeToken, out var entry))
        {
            return Task.FromResult(false);
        }
        if (entry.VerifiedAtUtc is null)
        {
            return Task.FromResult(false);
        }
        // A consumed entry is no longer "recently verified" — the one-shot
        // promise is enforced here as defence in depth alongside the
        // ConsumeAsync CAS so a callsite that forgot to call ConsumeAsync
        // still cannot replay a consumed token.
        if (entry.IsConsumed)
        {
            return Task.FromResult(false);
        }
        var now = _clock.UtcNow;
        if (entry.VerifiedAtUtc + DefaultPostVerifyWindow <= now)
        {
            _entries.TryRemove(challengeToken, out _);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<Result> ConsumeAsync(string? challengeToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(challengeToken))
        {
            return Task.FromResult(Result.Failure(
                ErrorCodes.CaptchaTokenMissing, "Challenge token is required."));
        }

        // CAS-loop the IsConsumed transition. The interesting race is two
        // concurrent gate requests presenting the SAME verified token within
        // the post-verify window — exactly one must succeed (Result.Success)
        // and the other must observe CaptchaAlreadyConsumed. TryUpdate's
        // value-comparison ensures only one thread sees the un-consumed entry
        // and transitions it to consumed.
        var now = _clock.UtcNow;
        while (true)
        {
            if (!_entries.TryGetValue(challengeToken, out var entry))
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Unknown challenge token."));
            }
            if (entry.VerifiedAtUtc is null)
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Challenge has not been verified yet."));
            }
            if (entry.ExpiresAtUtc <= now || entry.VerifiedAtUtc + DefaultPostVerifyWindow <= now)
            {
                _entries.TryRemove(challengeToken, out _);
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaTokenInvalid, "Challenge has expired."));
            }
            if (entry.IsConsumed)
            {
                return Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaAlreadyConsumed,
                    "Verified CAPTCHA token has already been consumed."));
            }

            var updated = entry with { IsConsumed = true };
            if (_entries.TryUpdate(challengeToken, updated, entry))
            {
                return Task.FromResult(Result.Success());
            }
            // Lost the CAS race — another thread either consumed the entry or
            // mutated it. Reload and re-evaluate; the next iteration short-
            // circuits to ALREADY_CONSUMED if our racing peer won.
        }
    }

    /// <summary>
    /// Iterates the dictionary and evicts entries whose <see cref="ChallengeEntry.ExpiresAtUtc"/>
    /// has elapsed. Called from <see cref="IssueAsync"/> on the natural-rate
    /// pacing of new issuance.
    /// </summary>
    /// <param name="now">Current UTC instant.</param>
    private void SweepExpired(DateTime now)
    {
        foreach (var kv in _entries)
        {
            // Evict entries that have either fully expired OR whose post-
            // verify window has lapsed — both classes are dead.
            var expired = kv.Value.ExpiresAtUtc <= now;
            var stalePostVerify = kv.Value.VerifiedAtUtc.HasValue
                && kv.Value.VerifiedAtUtc.Value + DefaultPostVerifyWindow <= now;
            if (expired || stalePostVerify)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// Generates a 24-byte URL-safe token (Base64URL-encoded) used as the
    /// dictionary key. The 192-bit keyspace is more than enough for unique
    /// issuance across the realistic concurrent-challenge surface.
    /// </summary>
    /// <returns>Opaque URL-safe token.</returns>
    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Generates a <see cref="CodeLength"/>-character challenge code from
    /// <see cref="CodeAlphabet"/>. The alphabet excludes visually-similar
    /// characters (0/O, 1/I/l) so user error is minimised.
    /// </summary>
    /// <returns>Random uppercase alphanumeric code.</returns>
    private static string NewCode()
    {
        Span<char> chars = stackalloc char[CodeLength];
        Span<byte> rng = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(rng);
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = CodeAlphabet[rng[i] % CodeAlphabet.Length];
        }
        return new string(chars);
    }

    /// <summary>
    /// Builds the SVG payload rendered by the client. The text is laid out
    /// across a 120x40 canvas; per-character X offsets and angles vary
    /// slightly to add visual texture without harming readability.
    /// </summary>
    /// <param name="code">The challenge code to render.</param>
    /// <returns>Self-contained SVG document text.</returns>
    internal static string BuildSvg(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 40\" width=\"120\" height=\"40\">");
        sb.Append("<rect width=\"120\" height=\"40\" fill=\"#f0f0f0\"/>");
        for (var i = 0; i < code.Length; i++)
        {
            var x = 10 + (i * 18);
            var rotation = ((i * 7) % 13) - 6;
            sb.Append("<text x=\"").Append(x).Append("\" y=\"28\" font-family=\"monospace\" font-size=\"24\" fill=\"#222\" transform=\"rotate(")
              .Append(rotation).Append(' ').Append(x).Append(",24)\">");
            sb.Append(System.Net.WebUtility.HtmlEncode(code[i].ToString()));
            sb.Append("</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Internal challenge bookkeeping record. Stored in the
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> values; immutable so
    /// updates copy-on-write via <c>with</c>.
    /// </summary>
    /// <param name="Answer">Case-normalised expected answer (uppercase).</param>
    /// <param name="ExpiresAtUtc">Hard expiry of the challenge.</param>
    /// <param name="VerifiedAtUtc">UTC instant of a successful verification; null until then.</param>
    /// <param name="IsConsumed">Set once the verified token is consumed by the gated endpoint.</param>
    private sealed record ChallengeEntry(
        string Answer,
        DateTime ExpiresAtUtc,
        DateTime? VerifiedAtUtc,
        bool IsConsumed);
}
