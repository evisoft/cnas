using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.PublicServices;

/// <summary>
/// R0511 — temporary stub of <see cref="IPccmGateway"/> for the anonymous
/// medical-certificate status surface. Returns hand-coded responses for two
/// sample certificate numbers so the public endpoint can be exercised end-to-end
/// before the real PCCM SOAP/REST integration is wired (deferred).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sample data.</b> Two certificates are recognised:
/// <list type="bullet">
///   <item>
///   <c>"PCCM-ACTIVE-001"</c> — Status="Active", 2026-05-01 → 2026-05-21,
///   issuer "IMSP Centrul Medical Sf. Maria".
///   </item>
///   <item>
///   <c>"PCCM-CANCELLED-002"</c> — Status="Cancelled", issued 2026-04-01,
///   issuer "IMSP Centrul Medical Sf. Maria".
///   </item>
/// </list>
/// Any other certificate number collapses to <c>Status="NotFound"</c>.
/// </para>
/// <para>
/// <b>Idnp / DOB disambiguators.</b> The stub does NOT verify the supplied IDNP
/// or DOB against an internal table — it accepts any non-empty IDNP + any DOB
/// for the two recognised numbers. The anti-enumeration discipline is therefore
/// enforced by the calling service rather than by the gateway; a real PCCM
/// integration MUST reject IDNP-mismatch with a <c>Status="NotFound"</c> shape
/// per the contract on <see cref="IPccmGateway"/>.
/// </para>
/// </remarks>
public sealed class MockPccmGateway : IPccmGateway
{
    /// <summary>Stable certificate-number used by the "Active" sample.</summary>
    public const string ActiveSampleCertificateNumber = "PCCM-ACTIVE-001";

    /// <summary>Stable certificate-number used by the "Cancelled" sample.</summary>
    public const string CancelledSampleCertificateNumber = "PCCM-CANCELLED-002";

    /// <summary>
    /// Stable display name of the seeded issuing institution. Used as the
    /// <see cref="PccmCertificateStatus.IssuerName"/> value of every recognised
    /// certificate sample.
    /// </summary>
    public const string SampleIssuerName = "IMSP Centrul Medical Sf. Maria";

    /// <inheritdoc />
    public Task<Result<PccmCertificateStatus>> LookupCertificateAsync(
        string certificateNumber,
        string idnp,
        DateOnly dateOfBirth,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(certificateNumber);
        ArgumentNullException.ThrowIfNull(idnp);
        _ = ct;
        _ = dateOfBirth;

        // The stub trims + uppercases the certificate number so callers can pass
        // human-typed input without exact-case requirements. The real PCCM
        // contract will likely impose case sensitivity; this leniency lives only
        // in the stub.
        var normalised = certificateNumber.Trim();

        if (string.Equals(normalised, ActiveSampleCertificateNumber, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<PccmCertificateStatus>.Success(new PccmCertificateStatus(
                CertificateNumber: ActiveSampleCertificateNumber,
                Status: "Active",
                IssuedDate: new DateOnly(2026, 5, 1),
                ValidFromDate: new DateOnly(2026, 5, 1),
                ValidToDate: new DateOnly(2026, 5, 21),
                IssuerName: SampleIssuerName)));
        }

        if (string.Equals(normalised, CancelledSampleCertificateNumber, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<PccmCertificateStatus>.Success(new PccmCertificateStatus(
                CertificateNumber: CancelledSampleCertificateNumber,
                Status: "Cancelled",
                IssuedDate: new DateOnly(2026, 4, 1),
                ValidFromDate: new DateOnly(2026, 4, 1),
                ValidToDate: new DateOnly(2026, 4, 14),
                IssuerName: SampleIssuerName)));
        }

        // Unknown certificate: collapse to NotFound per the anti-enumeration
        // contract. We still return Success — the absence of a match is a
        // legitimate business outcome, not an infrastructure failure.
        return Task.FromResult(Result<PccmCertificateStatus>.Success(new PccmCertificateStatus(
            CertificateNumber: normalised,
            Status: "NotFound",
            IssuedDate: null,
            ValidFromDate: null,
            ValidToDate: null,
            IssuerName: null)));
    }
}
