using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Cnas.Ps.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// <see cref="DelegatingHandler"/> that attaches the per-service X.509 client certificate
/// resolved from <see cref="ICertificateStore"/> to the outbound TLS handshake. When no
/// certificate is registered for the service, the handler passes the request through
/// unchanged so existing Bearer-token authentication continues to work — see
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> for the staged mTLS rollout.
/// </summary>
/// <remarks>
/// <para>
/// The certificate must be attached to the <em>primary</em> handler (the
/// <see cref="SocketsHttpHandler"/> or <see cref="HttpClientHandler"/> at the bottom of
/// the chain) because TLS happens at the socket layer; a <see cref="DelegatingHandler"/>
/// only sees the already-decrypted <see cref="HttpRequestMessage"/>. The expected wiring
/// is therefore:
/// <list type="bullet">
///   <item>Register the typed <see cref="HttpClient"/> via <c>IHttpClientFactory</c>.</item>
///   <item>Call <c>ConfigurePrimaryHttpMessageHandler</c> to build a
///   <see cref="SocketsHttpHandler"/> whose <see cref="SocketsHttpHandler.SslOptions"/>
///   carries the certificate resolved from <see cref="ICertificateStore"/>.</item>
///   <item>Add this handler via <c>AddHttpMessageHandler</c> so that requests for
///   services without a registered certificate are flagged in logs (for diagnostic
///   visibility) and the Bearer-fallback path stays observable.</item>
/// </list>
/// In other words, this <see cref="DelegatingHandler"/> does NOT mutate the inner
/// handler at send-time — that would be both racy (the handler is shared across all
/// requests on the client) and ineffective (TLS is already negotiated before the
/// outer pipeline runs). It exists to (a) provide a single place to assert the
/// presence/absence of a certificate per request and (b) surface diagnostic logging.
/// </para>
/// <para>
/// Wired via DI in a future refactor round; the foundation here gives every MGov
/// client a uniform extension point.
/// </para>
/// </remarks>
public sealed class ClientCertificateHttpHandler : DelegatingHandler
{
    private readonly ICertificateStore _store;
    private readonly string _serviceName;
    private readonly ILogger<ClientCertificateHttpHandler> _logger;

    /// <summary>
    /// Initialises the handler for the named MGov service.
    /// </summary>
    /// <param name="store">Certificate store resolved from DI.</param>
    /// <param name="serviceName">
    /// Stable service name (see <see cref="ICertificateStore"/> constants — e.g.
    /// <c>"mnotify"</c>, <c>"mlog"</c>).
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public ClientCertificateHttpHandler(
        ICertificateStore store,
        string serviceName,
        ILogger<ClientCertificateHttpHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _serviceName = serviceName;
        _logger = logger;
    }

    /// <summary>
    /// The MGov service name this handler resolves certificates for. Exposed for
    /// diagnostics and for the wiring layer that builds the primary handler.
    /// </summary>
    public string ServiceName => _serviceName;

    /// <summary>
    /// Returns the certificate that should be presented for this service on the next
    /// outbound call, or <c>null</c> if none is registered (Bearer fallback path).
    /// Called by the primary-handler factory in the DI composition root.
    /// </summary>
    /// <remarks>
    /// Failures (load error, thumbprint mismatch) are logged at warning level and
    /// surface as <c>null</c> so the primary handler does not attach a broken
    /// certificate. Callers that need to distinguish "missing" from "broken" should
    /// query <see cref="ICertificateStore.GetCertificate"/> directly.
    /// </remarks>
    public X509Certificate2? ResolveCertificate()
    {
        var probe = _store.TryGetCertificate(_serviceName);
        if (probe.IsFailure)
        {
            _logger.LogWarning(
                "mTLS certificate for service {Service} failed to load ({Code}): {Message}",
                _serviceName, probe.ErrorCode, probe.ErrorMessage);
            return null;
        }
        return probe.Value;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Diagnostic only — the certificate is attached at the primary-handler layer
        // (SocketsHttpHandler.SslOptions) at DI composition time, not here.
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            var probe = _store.TryGetCertificate(_serviceName);
            var hasCert = probe.IsSuccess && probe.Value is not null;
            _logger.LogTrace(
                "Outbound {Method} {Uri} for service {Service}; mTLS={Mtls}.",
                request.Method, request.RequestUri, _serviceName, hasCert ? "on" : "off");
        }
        return base.SendAsync(request, cancellationToken);
    }
}
