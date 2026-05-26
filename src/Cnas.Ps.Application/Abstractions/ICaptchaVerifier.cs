using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Server-side verifier for CAPTCHA tokens submitted by anonymous clients on protected
/// public endpoints (UC01, UC02). Implementations call out to the provider (Cloudflare
/// Turnstile in production) and return a clean <see cref="Result"/> regardless of network
/// failures or provider errors — exceptions are reserved for genuinely exceptional
/// situations (OOM, programmer errors), per CLAUDE.md §2.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed semantics.</b> A degraded CAPTCHA service must NOT become an open door
/// for abuse — every implementation returns
/// <see cref="ErrorCodes.CaptchaProviderUnreachable"/> on network/transport/parse failure
/// and the calling filter maps that to HTTP 503. The rate limiter remains the only abuse
/// guard while the provider is down; opening the gate on unreachable would invert the
/// security posture of the entire anonymous surface.
/// </para>
/// <para>
/// <b>Token hygiene.</b> Implementations MUST NEVER log the raw token value (it is a
/// short-lived secret bearer credential) and MUST NEVER echo any portion of it back to
/// the caller. The provider's <c>error-codes</c> array MAY be joined into the failure
/// message for operator diagnostics, but the token itself stays opaque.
/// </para>
/// <para>
/// Failure modes the caller MUST handle:
/// <list type="bullet">
///   <item><see cref="ErrorCodes.CaptchaTokenMissing"/> — no token in the request.</item>
///   <item><see cref="ErrorCodes.CaptchaTokenInvalid"/> — token rejected by the provider.</item>
///   <item><see cref="ErrorCodes.CaptchaProviderUnreachable"/> — network/provider error; fail closed.</item>
/// </list>
/// </para>
/// </remarks>
public interface ICaptchaVerifier
{
    /// <summary>
    /// Verifies a CAPTCHA token against the configured provider. Returns
    /// <see cref="Result.Success()"/> on a valid token; <see cref="Result.Failure"/>
    /// with one of the documented error codes otherwise.
    /// </summary>
    /// <param name="token">
    /// Token from the client's challenge widget. May be <c>null</c>, empty, or
    /// whitespace — in which case the verifier short-circuits to
    /// <see cref="ErrorCodes.CaptchaTokenMissing"/> without making an HTTP call.
    /// </param>
    /// <param name="remoteIp">
    /// Optional remote IP for provider-side abuse correlation. When supplied, it is sent
    /// to the provider in the <c>remoteip</c> form field (Turnstile uses this to detect
    /// distributed abuse patterns). When <c>null</c> or whitespace, the field is omitted
    /// from the request.
    /// </param>
    /// <param name="ct">Cancellation token honoured by the underlying HTTP call.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the provider accepts the token;
    /// <see cref="ErrorCodes.CaptchaTokenMissing"/> when <paramref name="token"/> is
    /// null/empty/whitespace; <see cref="ErrorCodes.CaptchaTokenInvalid"/> when the
    /// provider returns <c>success: false</c>;
    /// <see cref="ErrorCodes.CaptchaProviderUnreachable"/> when the provider call fails
    /// for any other reason (transport error, timeout, HTTP 5xx, malformed response).
    /// </returns>
    Task<Result> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default);
}
