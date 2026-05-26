using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicServices;

/// <summary>
/// R0511 / TOR CF 02.01 — thin facade over PCCM specialised for the anonymous
/// "lookup my medical certificate status" surface. Distinct from
/// <see cref="Cnas.Ps.Application.External.IPccmClient"/>, which exposes the
/// authenticated bulk query used by intake services. This single-shot query
/// returns the status of one certificate identified by its PCCM-assigned
/// number, disambiguated by the citizen's IDNP and date of birth.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration contract.</b> Implementations MUST NOT distinguish
/// "certificate unknown" from "certificate exists but IDNP doesn't match" in
/// their successful response — both collapse to a
/// <see cref="PccmCertificateStatus"/> with <c>Status="NotFound"</c>.
/// Distinguishing the two would leak enrolment information to an attacker
/// scraping the public endpoint.
/// </para>
/// <para>
/// <b>Availability contract.</b> When PCCM cannot be reached (MConnect down,
/// HTTP 5xx, malformed response) the implementation returns a failed
/// <see cref="Result{T}"/> with <see cref="ErrorCodes.MConnectFailed"/> or
/// equivalent. The calling service maps that to a stable
/// <c>PCCM_UNAVAILABLE</c> error code so the UI can render a "service
/// temporarily unavailable, try again later" prompt rather than a generic
/// internal-error page.
/// </para>
/// </remarks>
public interface IPccmGateway
{
    /// <summary>
    /// Looks up a single medical certificate by its PCCM number, disambiguated
    /// by the citizen's IDNP and date of birth. The result is a value object
    /// describing the certificate's lifecycle metadata — never the citizen's
    /// PII.
    /// </summary>
    /// <param name="certificateNumber">PCCM-assigned certificate number.</param>
    /// <param name="idnp">Citizen's 13-digit IDNP (already validated by the caller).</param>
    /// <param name="dateOfBirth">Citizen's date of birth.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a <see cref="PccmCertificateStatus"/> — when the certificate
    /// is not in PCCM (or the disambiguators don't match) the status carries
    /// the <see cref="PccmCertificateStatus.Status"/> = <c>"NotFound"</c>
    /// shape. On infrastructure failure a failed <see cref="Result{T}"/> with
    /// an MConnect / network error code.
    /// </returns>
    Task<Result<PccmCertificateStatus>> LookupCertificateAsync(
        string certificateNumber,
        string idnp,
        DateOnly dateOfBirth,
        CancellationToken ct = default);
}

/// <summary>
/// R0511 — typed PCCM-side response carrying the certificate's lifecycle
/// metadata. Carries NO citizen PII — only the certificate's own state and
/// (optionally) the issuing institution's display name.
/// </summary>
/// <param name="CertificateNumber">PCCM-assigned certificate number (echo of the request).</param>
/// <param name="Status">
/// Stable lifecycle code: <c>"Active"</c>, <c>"Closed"</c>, <c>"Cancelled"</c>,
/// or <c>"NotFound"</c>.
/// </param>
/// <param name="IssuedDate">Date the certificate was originally issued.</param>
/// <param name="ValidFromDate">First date of prescribed leave (inclusive).</param>
/// <param name="ValidToDate">Last date of prescribed leave (inclusive).</param>
/// <param name="IssuerName">Display name of the issuing institution.</param>
public sealed record PccmCertificateStatus(
    string CertificateNumber,
    string Status,
    DateOnly? IssuedDate,
    DateOnly? ValidFromDate,
    DateOnly? ValidToDate,
    string? IssuerName);
