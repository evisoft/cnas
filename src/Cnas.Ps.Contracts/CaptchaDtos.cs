using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0507 / TOR CF 01.10 — self-issued CAPTCHA challenge DTOs for anonymous
// public endpoints (Catalog search). Distinct from the existing
// TurnstileCaptchaVerifier path which verifies tokens minted by Cloudflare —
// these wrappers ship a self-contained challenge that the SI PS surface can
// issue offline. Contracts only depend on Contracts.Security; no Core types.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0507 — outbound projection returned by <c>GET /api/captcha/challenge</c>.
/// Carries a freshly issued opaque token plus the encoded image bytes the
/// client must render so the user can read the code.
/// </summary>
/// <param name="ChallengeToken">
/// Opaque single-use token. The client MUST include it (alongside the
/// <c>Answer</c> the user reads from the image) in the subsequent
/// <c>POST /api/captcha/verify</c> request. The token expires after 5 minutes
/// and is invalidated on first successful verification (one-shot).
/// </param>
/// <param name="ImageBase64">
/// Base64-encoded image payload. The default implementation ships an SVG
/// image — <c>MimeType</c> is the indicator. Clients embed it via
/// <c>&lt;img src="data:{mime};base64,{ImageBase64}" /&gt;</c>.
/// </param>
/// <param name="MimeType">
/// IANA media type of the embedded image (e.g. <c>image/svg+xml</c>,
/// <c>image/png</c>). Set by the implementation; clients SHOULD render
/// whichever the server returns rather than assuming a fixed type.
/// </param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record CaptchaIssueDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ChallengeToken,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ImageBase64,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string MimeType);

/// <summary>
/// R0507 — inbound input envelope for <c>POST /api/captcha/verify</c>.
/// Submitting the wrong <see cref="Answer"/> for the
/// <see cref="ChallengeToken"/> consumes neither — the client may retry with
/// a fresh attempt until the TTL elapses. A successful verification stamps
/// the token consumed so a replay attempt yields a failure.
/// </summary>
/// <param name="ChallengeToken">Token returned by a prior <c>/api/captcha/challenge</c> call.</param>
/// <param name="Answer">User-supplied code; case-insensitive comparison.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record CaptchaVerifyInputDto(
    string ChallengeToken,
    string Answer);
