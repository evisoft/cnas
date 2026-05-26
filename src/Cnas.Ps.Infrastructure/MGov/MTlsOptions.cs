namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Configuration root for the mTLS / client-certificate transport hardening of the
/// MGov service suite. Bound from <c>Cnas:MGov:Mtls</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry under <see cref="Certificates"/> maps a stable service name
/// (see <see cref="Cnas.Ps.Application.Abstractions.ICertificateStore"/> constants) to
/// a PFX / PKCS#12 file on disk plus an optional decrypt password and optional
/// thumbprint pin. Service names are case-insensitive because the
/// <c>IConfiguration</c> binder uses an ordinal-case-sensitive dictionary by default
/// and operators routinely vary casing in JSON / YAML.
/// </para>
/// <para>
/// The integration is opt-in per service: leaving a service out of the dictionary
/// causes <see cref="Cnas.Ps.Application.Abstractions.ICertificateStore.TryGetCertificate"/>
/// to return a successful <c>null</c>, which the per-service HTTP handler interprets
/// as &quot;use Bearer authentication for now&quot;. This keeps the universal mTLS
/// rollout backwards-compatible with the existing Bearer-header flow.
/// </para>
/// </remarks>
public sealed class MTlsOptions
{
    /// <summary>Section name in app settings.</summary>
    public const string SectionName = "Cnas:MGov:Mtls";

    /// <summary>
    /// Per-service certificate registrations keyed by stable service name. Lookup is
    /// case-insensitive so operators can write <c>"MNotify"</c> in JSON without
    /// breaking the binding.
    /// </summary>
    public Dictionary<string, MTlsCertificateOptions> Certificates { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-service mTLS client-certificate registration.
/// </summary>
/// <param name="Path">
/// Absolute or working-directory-relative path to the PFX / PKCS#12 file. Required.
/// </param>
/// <param name="Password">
/// Optional decrypt password for the PFX. Pass <c>null</c> when the file is unencrypted.
/// </param>
/// <param name="Thumbprint">
/// Optional SHA-1 thumbprint (40 hex chars) pinned by configuration. When non-null, the
/// loaded certificate's thumbprint must match exactly (case-insensitive). A mismatch
/// surfaces as <see cref="Cnas.Ps.Core.Common.ErrorCodes.CertificateThumbprintMismatch"/>
/// and the certificate is rejected — operators must investigate before traffic resumes.
/// </param>
public sealed record MTlsCertificateOptions(string Path, string? Password, string? Thumbprint);
