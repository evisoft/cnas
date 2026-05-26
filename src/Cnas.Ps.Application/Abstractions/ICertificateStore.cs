using System.Security.Cryptography.X509Certificates;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Resolves the X.509 client certificate that should be presented when calling the
/// named MGov service over mTLS. Implementations load PFX / PKCS#12 material from a
/// pluggable backend (filesystem, secrets manager, key vault) and cache the parsed
/// <see cref="X509Certificate2"/> for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// MGov (MPass, MSign, MPay, MConnect, MNotify, MLog, MConnect Events, MDocs, MCabinet)
/// authenticates CNAS using mutual TLS rather than Bearer tokens — see
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"mTLS / Client Certificates". The certificate
/// itself is the identity; the per-service registration in
/// <c>Cnas:MGov:Mtls:Certificates:&lt;serviceName&gt;</c> maps a stable service name
/// (the constants on this interface) to a PFX file + optional password + optional
/// thumbprint pin.
/// </para>
/// <para>
/// Two access shapes are exposed so callers can choose between strict and best-effort
/// semantics:
/// <list type="bullet">
///   <item><see cref="GetCertificate"/> — required certificate; missing config is a failure.</item>
///   <item><see cref="TryGetCertificate"/> — optional certificate; missing config is a successful <c>null</c>.</item>
/// </list>
/// The "Try" variant exists so the per-service <see cref="System.Net.Http.DelegatingHandler"/>
/// installed on every MGov HttpClient can decide at request time whether to attach a
/// client certificate (mTLS path) or fall through unchanged (Bearer fallback) without
/// having to distinguish &quot;not configured&quot; from &quot;configured but broken&quot;.
/// </para>
/// </remarks>
public interface ICertificateStore
{
    /// <summary>Stable service name for MPass (OIDC + assertion).</summary>
    public const string MPass = "mpass";

    /// <summary>Stable service name for MSign (qualified e-signature).</summary>
    public const string MSign = "msign";

    /// <summary>Stable service name for MPay (electronic payments).</summary>
    public const string MPay = "mpay";

    /// <summary>Stable service name for MNotify (citizen notifications).</summary>
    public const string MNotify = "mnotify";

    /// <summary>Stable service name for MLog (central government journaling).</summary>
    public const string MLog = "mlog";

    /// <summary>Stable service name for the synchronous MConnect interoperability bus.</summary>
    public const string MConnect = "mconnect";

    /// <summary>Stable service name for the asynchronous MConnect Events stream (CloudEvents v1.0).</summary>
    public const string MConnectEvents = "mconnect-events";

    /// <summary>Stable service name for MDocs (managed document storage).</summary>
    public const string MDocs = "mdocs";

    /// <summary>Stable service name for MCabinet (citizen portal mirror).</summary>
    public const string MCabinet = "mcabinet";

    /// <summary>
    /// Resolves the client certificate registered for <paramref name="serviceName"/>.
    /// Returns a failure with <see cref="ErrorCodes.CertificateNotConfigured"/> when no
    /// certificate is registered for the service.
    /// </summary>
    /// <param name="serviceName">
    /// Stable, case-insensitive service name (use the constants on this interface).
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the backend call.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the parsed certificate on success;
    /// <see cref="ErrorCodes.CertificateNotConfigured"/> if the service is unknown;
    /// <see cref="ErrorCodes.CertificateLoadFailed"/> if the PFX cannot be parsed;
    /// <see cref="ErrorCodes.CertificateThumbprintMismatch"/> if the loaded certificate
    /// does not match the configured thumbprint pin.
    /// </returns>
    Result<X509Certificate2> GetCertificate(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort variant of <see cref="GetCertificate"/>. Returns
    /// <see cref="Result{T}.Success(T)"/> with a <c>null</c> value when no certificate is
    /// registered for <paramref name="serviceName"/>, so HTTP handlers can decide to fall
    /// through to Bearer-token authentication without distinguishing &quot;not
    /// configured&quot; from &quot;configured but broken&quot;.
    /// </summary>
    /// <param name="serviceName">
    /// Stable, case-insensitive service name (use the constants on this interface).
    /// </param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the parsed certificate when one is
    /// registered and valid; <see cref="Result{T}.Success(T)"/> with <c>null</c> when no
    /// certificate is registered (legitimate Bearer fallback);
    /// <see cref="ErrorCodes.CertificateLoadFailed"/> or
    /// <see cref="ErrorCodes.CertificateThumbprintMismatch"/> when registration exists
    /// but the certificate is unusable.
    /// </returns>
    Result<X509Certificate2?> TryGetCertificate(string serviceName);
}
