using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Captcha;

/// <summary>
/// R0507 / TOR CF 01.10 — server-side self-issued CAPTCHA challenge / verify
/// pair. Distinct from
/// <see cref="Cnas.Ps.Application.Abstractions.ICaptchaVerifier"/> (which
/// validates tokens minted by an external provider such as Cloudflare
/// Turnstile); this seam mints and validates challenges WHOLLY in-process so
/// the anti-abuse posture on the public catalog search survives even when
/// the external provider is unreachable.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-shot tokens.</b> A successful
/// <see cref="VerifyAsync(string, string, CancellationToken)"/> consumes the
/// stored entry — a replay returns
/// <see cref="ErrorCodes.CaptchaTokenInvalid"/>. Tokens also auto-expire
/// after the implementation-defined TTL (5 minutes by default).
/// </para>
/// <para>
/// <b>Wire-protocol contract.</b> Anonymous clients call
/// <c>GET /api/captcha/challenge</c> to get a token + image, render the
/// image for the user, then submit
/// <c>POST /api/captcha/verify</c> with the user's answer. On success the
/// gated downstream endpoint (e.g. public-catalog list) accepts the same
/// <c>X-Captcha-Token</c> header for up to
/// <c>CaptchaPostVerifyWindowMinutes</c> minutes.
/// </para>
/// </remarks>
public interface ICaptchaChallengeService
{
    /// <summary>
    /// Mints a fresh challenge: stores the answer keyed by an opaque token
    /// and returns the token + an image the client can render so the user
    /// can read the code.
    /// </summary>
    /// <param name="ct">Cancellation token honoured by the implementation.</param>
    /// <returns>The freshly issued challenge.</returns>
    Task<CaptchaIssueDto> IssueAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies a user answer against the stored challenge. Consumes the
    /// stored entry on success so a replay fails.
    /// </summary>
    /// <param name="challengeToken">Token returned by <see cref="IssueAsync"/>.</param>
    /// <param name="answer">User-supplied answer (case-insensitive match).</param>
    /// <param name="ct">Cancellation token honoured by the implementation.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the token + answer match an active
    /// (non-expired, non-consumed) challenge.
    /// <see cref="ErrorCodes.CaptchaTokenMissing"/> when either field is
    /// null/empty/whitespace.
    /// <see cref="ErrorCodes.CaptchaTokenInvalid"/> when the token is
    /// unknown, expired, already consumed, or the answer doesn't match.
    /// </returns>
    Task<Result> VerifyAsync(string? challengeToken, string? answer, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> iff the token was successfully verified within the
    /// recent past (i.e. its post-verify stamp is still inside the
    /// configured grace window). Used by the public-catalog gate to accept
    /// the X-Captcha-Token header on the downstream call. Returns
    /// <c>false</c> for unknown / expired / un-verified tokens.
    /// </summary>
    /// <param name="challengeToken">Token returned by <see cref="IssueAsync"/>.</param>
    /// <param name="ct">Cancellation token honoured by the implementation.</param>
    /// <returns><c>true</c> iff the token is currently verified.</returns>
    Task<bool> IsRecentlyVerifiedAsync(string? challengeToken, CancellationToken ct = default);

    /// <summary>
    /// Atomically flips the verified token's <c>IsConsumed</c> flag to
    /// <c>true</c> so that subsequent requests inside the post-verify window
    /// cannot replay the same token through the downstream gate. The flip is
    /// CAS-guarded — a second concurrent consume call returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.CaptchaAlreadyConsumed"/>
    /// rather than silently succeeding.
    /// </summary>
    /// <param name="challengeToken">Token returned by <see cref="IssueAsync"/>.</param>
    /// <param name="ct">Cancellation token honoured by the implementation.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the verified token was flipped from
    /// un-consumed to consumed.
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.CaptchaTokenMissing"/> when the
    /// token field is null/empty/whitespace.
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.CaptchaTokenInvalid"/> when the
    /// token is unknown, expired, or has never been verified.
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.CaptchaAlreadyConsumed"/> when
    /// the token was verified but is already consumed.
    /// </returns>
    Task<Result> ConsumeAsync(string? challengeToken, CancellationToken ct = default);
}
