using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// PCCM — Sistemul Informațional Concedii Medicale (electronic Medical Leave / Sick-Note
/// system). Source of medical certificates (concedii medicale) issued by accredited
/// providers.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>PCCM.GetMedicalCertificates</c>. See TOR
/// §2.1 (item 5 in the list of 11 external systems). The result list may be empty when
/// the IDNP has no certificates in the supplied UTC date window.
/// </remarks>
public interface IPccmClient
{
    /// <summary>
    /// Retrieves medical certificates issued for the supplied IDNP overlapping the
    /// inclusive UTC window <paramref name="fromUtc"/>..<paramref name="toUtc"/>.
    /// </summary>
    /// <param name="idnp">Insured person's IDNP.</param>
    /// <param name="fromUtc">Inclusive lower bound of the search window (UTC).</param>
    /// <param name="toUtc">Inclusive upper bound of the search window (UTC).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see cref="Result{T}"/> wrapping the certificates list (possibly empty).</returns>
    Task<Result<IReadOnlyList<PccmCertificate>>> GetCertificatesAsync(string idnp, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

/// <summary>
/// One medical certificate as returned by <see cref="IPccmClient"/>.
/// </summary>
/// <param name="CertificateNumber">PCCM-assigned certificate number (stable identifier).</param>
/// <param name="IssuedOn">Date the certificate was issued.</param>
/// <param name="StartDate">First date of the prescribed medical leave.</param>
/// <param name="EndDate">Last date of the prescribed medical leave (inclusive).</param>
/// <param name="Diagnosis">ICD-10 diagnosis code (e.g. "J11.1").</param>
/// <param name="IssuerCode">PCCM provider code of the issuing institution.</param>
public sealed record PccmCertificate(string CertificateNumber, DateOnly IssuedOn, DateOnly StartDate, DateOnly EndDate, string Diagnosis, string IssuerCode);
