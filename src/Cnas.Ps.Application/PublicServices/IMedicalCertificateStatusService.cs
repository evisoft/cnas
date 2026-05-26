using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicServices;

/// <summary>
/// R0511 / TOR CF 02.01 — anonymous medical-certificate status lookup. Drives
/// the public, unauthenticated <c>POST /api/public/medical-certificate-status</c>
/// endpoint. The citizen supplies the certificate number plus IDNP + DOB as
/// disambiguators; the service forwards the request to PCCM via
/// <see cref="IPccmGateway"/> and returns a sanitised status DTO that never
/// echoes PII.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration discipline.</b> Three failure shapes collapse into a
/// single <c>Status="NotFound"</c> response: unknown certificate, mismatched
/// IDNP, mismatched DOB. Distinguishing them would let a scraper differentiate
/// "this IDNP has medical certificates on file" from "wrong number" — that
/// difference is itself sensitive information.
/// </para>
/// <para>
/// <b>Defensive failures.</b> Captcha-rejection and IDNP-malformed are
/// validation failures (<see cref="ErrorCodes.CaptchaTokenInvalid"/> /
/// <see cref="ErrorCodes.CaptchaTokenMissing"/> / <see cref="ErrorCodes.InvalidIdnp"/>),
/// returned as <see cref="Result{T}.Failure(string, string)"/> so the caller
/// can render a useful prompt. PCCM unavailability surfaces as
/// <see cref="ErrorCodes.MConnectFailed"/> — the UI maps that to a "service
/// temporarily unavailable" message.
/// </para>
/// <para>
/// <b>Audit.</b> Every lookup writes a <c>PUBLIC.MEDICAL_CERT_LOOKUP</c> Notice
/// row carrying the SHA-256 hash of the certificate number (never the
/// plaintext) plus the resulting status code. The IDNP is never logged.
/// </para>
/// </remarks>
public interface IMedicalCertificateStatusService
{
    /// <summary>
    /// Verifies the captcha, validates the IDNP, calls PCCM, and projects the
    /// result into a sanitised DTO. Always writes one audit row.
    /// </summary>
    /// <param name="request">Lookup envelope from the public endpoint.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a <see cref="MedicalCertificateStatusDto"/> with the
    /// certificate's lifecycle metadata (or <c>Status="NotFound"</c> on any
    /// disambiguator mismatch). On captcha rejection, validation failure, or
    /// PCCM unavailability a failed <see cref="Result{T}"/> with the
    /// appropriate stable error code.
    /// </returns>
    Task<Result<MedicalCertificateStatusDto>> LookupAsync(
        MedicalCertificateLookupDto request,
        CancellationToken ct = default);
}
