using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// SOAP adapter for MSign — the government qualified-electronic-signature service.
/// Speaks the WS-I Basic Profile 1.1 contract published at
/// <c>https://msign.gov.md/MSign.svc?singleWsdl</c>. Authentication is mTLS only
/// (the X.509 client certificate is the identity); no <c>Authorization</c> header is
/// ever sent.
/// </summary>
/// <remarks>
/// <para>
/// The real protocol is two-phase:
/// </para>
/// <list type="number">
///   <item>
///     <see cref="PostSignRequestAsync"/> — server-to-server SOAP call returning a
///     <c>RequestId</c> and a redirect URL. The CNAS UI then redirects the signer's
///     browser to that URL.
///   </item>
///   <item>
///     The signer completes the ceremony in the MSign portal. MSign then POSTs a
///     callback to the CNAS-side endpoint
///     (<c>Cnas.Ps.Api.Controllers.MSignCallbackController</c>) and simultaneously
///     redirects the signer's browser back to the supplied return URL.
///   </item>
///   <item>
///     <see cref="GetSignResponseAsync"/> — server-to-server SOAP call retrieving
///     the signature bytes + signer metadata.
///   </item>
/// </list>
/// <para>
/// The legacy <see cref="SignAsync"/> entry point is preserved as a back-compat shim
/// — it drives the whole flow internally and polls <see cref="IsRequestReadyAsync"/>
/// in a tight loop while waiting on the signer. This is inefficient (it blocks the
/// caller for up to ~30 seconds and is fundamentally incompatible with a UI-driven
/// signing ceremony) and is documented as deprecated; new code should call the
/// two-phase API directly so the browser redirect is correctly threaded through the
/// UI layer.
/// </para>
/// <para>
/// SOAP envelopes are hand-rolled with <see cref="XmlWriter"/> rather than the
/// auto-generated <c>System.ServiceModel</c> proxy because (a) the official
/// <c>Egov.Integrations.MSign.Soap</c> NuGet package is not available on public feeds
/// and (b) hand-rolling lets us interop with the WS-I envelope shape while keeping
/// dependencies to a minimum.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed-client; primary handler attaches the mTLS cert.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; never receives the request/response body (PII).</param>
/// <param name="clock">UTC clock — used for the <c>X-Request-Date</c> header.</param>
/// <param name="auditService">
/// R0112 — optional audit sink. When supplied, every
/// <see cref="VerifySignatureAsync(byte[], MSignVerifyOptions, CancellationToken)"/> call
/// emits a Sensitive <c>MSIGN.SIGNATURE_VERIFIED</c> row carrying the outcome. <c>null</c>-safe
/// so legacy compositions pre-dating the verification feature keep compiling.
/// </param>
public sealed class MSignClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MSignClient> logger,
    ICnasTimeProvider clock,
    IAuditService? auditService = null) : IMSignClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MSignClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService? _auditService = auditService;

    /// <summary>
    /// Sentinel error list returned when the PKCS#7 envelope parses but carries no
    /// signer certificate. Cached as a single allocation per process to satisfy CA1861.
    /// </summary>
    private static readonly IReadOnlyList<string> SignerMissingErrors = new[] { "SignerCertificateMissing" };

    /// <summary>
    /// SOAP envelope namespace (WS-I Basic Profile 1.1 — SOAP 1.1).
    /// </summary>
    private const string SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// Service-contract namespace used by MSign for operation request / response
    /// elements (e.g. <c>PostSignRequest</c>, <c>GetSignResponse</c>,
    /// <c>IsRequestReady</c>).
    /// </summary>
    private const string MSignContractNs = "http://egov.md/MSign";

    /// <summary>
    /// Default interval between <see cref="IsRequestReadyAsync"/> polls inside the
    /// legacy <see cref="SignAsync"/> shim. Overridable in tests.
    /// </summary>
    public TimeSpan LegacyPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default ceiling on the number of <see cref="IsRequestReadyAsync"/> polls inside
    /// the legacy <see cref="SignAsync"/> shim — 30 with a 1s interval ≈ 30 seconds.
    /// Overridable in tests so a poll-timeout assertion can run in milliseconds.
    /// </summary>
    public int LegacyMaxPollIterations { get; set; } = 30;

    /// <inheritdoc />
    public async Task<Result<MSignPostSignResult>> PostSignRequestAsync(
        MSignPostSignRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.MSignBaseUrl))
        {
            _logger.LogWarning("MSign called without configured base URL — returning MSIGN_FAILED.");
            return Result<MSignPostSignResult>.Failure(ErrorCodes.MSignFailed, "BaseUrl not configured");
        }
        if (request.DocumentBytes is null || request.DocumentBytes.Length == 0)
        {
            return Result<MSignPostSignResult>.Failure(ErrorCodes.ValidationFailed, "DocumentBytes is required.");
        }
        if (string.IsNullOrWhiteSpace(request.DocumentName))
        {
            return Result<MSignPostSignResult>.Failure(ErrorCodes.ValidationFailed, "DocumentName is required.");
        }
        if (request.ReturnUrl is null)
        {
            return Result<MSignPostSignResult>.Failure(ErrorCodes.ValidationFailed, "ReturnUrl is required.");
        }

        var body = BuildPostSignRequestBody(request);
        var correlationId = request.CorrelationId ?? MGovHttp.DeriveCorrelationId(body);

        var soapResult = await SendSoapAsync(
            operation: "PostSignRequest",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result<MSignPostSignResult>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var parsed = ParsePostSignResponse(soapResult.Value);
            return Result<MSignPostSignResult>.Success(parsed);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(ex, "MSign PostSignRequest returned an unparseable SOAP body (correlation {Correlation}).", correlationId);
            return Result<MSignPostSignResult>.Failure(ErrorCodes.MSignFailed, "MSign returned a malformed PostSignRequest response.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MSignGetSignResult>> GetSignResponseAsync(
        string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Result<MSignGetSignResult>.Failure(ErrorCodes.ValidationFailed, "RequestId is required.");
        }
        if (string.IsNullOrWhiteSpace(_options.MSignBaseUrl))
        {
            _logger.LogWarning("MSign GetSignResponse called without configured base URL — returning MSIGN_FAILED.");
            return Result<MSignGetSignResult>.Failure(ErrorCodes.MSignFailed, "BaseUrl not configured");
        }

        var body = BuildSingleRequestIdBody("GetSignResponse", requestId);
        var correlationId = MGovHttp.DeriveCorrelationId(body);

        var soapResult = await SendSoapAsync(
            operation: "GetSignResponse",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result<MSignGetSignResult>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var parsed = ParseGetSignResponse(soapResult.Value);
            return Result<MSignGetSignResult>.Success(parsed);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "MSign GetSignResponse returned an unparseable SOAP body (correlation {Correlation}).", correlationId);
            return Result<MSignGetSignResult>.Failure(ErrorCodes.MSignFailed, "MSign returned a malformed GetSignResponse response.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsRequestReadyAsync(
        string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Result<bool>.Failure(ErrorCodes.ValidationFailed, "RequestId is required.");
        }
        if (string.IsNullOrWhiteSpace(_options.MSignBaseUrl))
        {
            _logger.LogWarning("MSign IsRequestReady called without configured base URL — returning MSIGN_FAILED.");
            return Result<bool>.Failure(ErrorCodes.MSignFailed, "BaseUrl not configured");
        }

        var body = BuildSingleRequestIdBody("IsRequestReady", requestId);
        var correlationId = MGovHttp.DeriveCorrelationId(body);

        var soapResult = await SendSoapAsync(
            operation: "IsRequestReady",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result<bool>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var ready = ParseIsRequestReadyResponse(soapResult.Value);
            return Result<bool>.Success(ready);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "MSign IsRequestReady returned an unparseable SOAP body (correlation {Correlation}).", correlationId);
            return Result<bool>.Failure(ErrorCodes.MSignFailed, "MSign returned a malformed IsRequestReady response.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MSignReceipt>> SignAsync(MSignRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Legacy shim: drive the two-phase flow internally. This is fundamentally
        // incompatible with a browser-driven signing ceremony — the citizen never sees
        // the redirect — but is preserved so existing call sites compile unchanged.
        // The return URL is a synthetic placeholder; the shim only retrieves the
        // signature once IsRequestReady reports true (i.e. when the upstream is
        // running in an auto-sign / system-cert mode).
        var post = await PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: request.PayloadHash,
            DocumentName: string.IsNullOrWhiteSpace(request.Reason) ? "payload" : request.Reason,
            ContentType: "application/octet-stream",
            Mode: MSignContentMode.Hash,
            ReturnUrl: new Uri("https://localhost/legacy-shim/no-redirect"),
            CorrelationId: null), cancellationToken).ConfigureAwait(false);
        if (post.IsFailure)
        {
            return Result<MSignReceipt>.Failure(post.ErrorCode!, post.ErrorMessage!);
        }

        var ready = false;
        for (var i = 0; i < LegacyMaxPollIterations; i++)
        {
            var probe = await IsRequestReadyAsync(post.Value.RequestId, cancellationToken).ConfigureAwait(false);
            if (probe.IsFailure)
            {
                return Result<MSignReceipt>.Failure(probe.ErrorCode!, probe.ErrorMessage!);
            }
            if (probe.Value)
            {
                ready = true;
                break;
            }
            try
            {
                await Task.Delay(LegacyPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return Result<MSignReceipt>.Failure(ErrorCodes.MSignFailed, "MSign signing was cancelled while not ready.");
            }
        }

        if (!ready)
        {
            return Result<MSignReceipt>.Failure(ErrorCodes.MSignFailed,
                "MSign signing request " + post.Value.RequestId + " is not ready after the configured poll budget.");
        }

        var fetched = await GetSignResponseAsync(post.Value.RequestId, cancellationToken).ConfigureAwait(false);
        if (fetched.IsFailure)
        {
            return Result<MSignReceipt>.Failure(fetched.ErrorCode!, fetched.ErrorMessage!);
        }

        return Result<MSignReceipt>.Success(new MSignReceipt(
            Signature: fetched.Value.SignatureBytes,
            ProtocolReference: post.Value.RequestId,
            SignedAt: new DateTimeOffset(fetched.Value.Metadata.SignedAtUtc, TimeSpan.Zero)));
    }

    /// <inheritdoc />
    public async Task<Result<bool>> VerifyAsync(byte[] payloadHash, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloadHash);
        ArgumentNullException.ThrowIfNull(signature);

        if (string.IsNullOrWhiteSpace(_options.MSignBaseUrl))
        {
            _logger.LogWarning("MSign Verify called without configured base URL — returning MSIGN_FAILED.");
            return Result<bool>.Failure(ErrorCodes.MSignFailed, "BaseUrl not configured");
        }

        // VerifySignature is not part of the production WSDL; map to a SOAP call
        // shaped like the other operations so the wiring stays uniform. Until the
        // production envelope is published, return failure so callers fall back to a
        // local cryptographic verification path.
        await Task.CompletedTask.ConfigureAwait(false);
        return Result<bool>.Failure(ErrorCodes.MSignFailed, "VerifySignature SOAP operation is not yet wired.");
    }

    /// <inheritdoc />
    public async Task<Result<SignatureVerificationResult>> VerifySignatureAsync(
        byte[] signedPayload, MSignVerifyOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signedPayload);
        ArgumentNullException.ThrowIfNull(options);

        var verification = PerformVerification(signedPayload, options);

        // Counter is tagged with the boolean outcome; cardinality bounded by 2.
        CnasMeter.MSignVerifyResult.Add(
            1,
            new KeyValuePair<string, object?>("result", verification.IsValid ? "valid" : "invalid"));

        if (_auditService is not null)
        {
            var details = JsonSerializer.Serialize(new
            {
                subjectCn = verification.SubjectCn,
                issuerCn = verification.IssuerCn,
                result = verification.IsValid ? "valid" : "invalid",
                errors = verification.ValidationErrors,
            });
            _ = await _auditService.RecordAsync(
                eventCode: "MSIGN.SIGNATURE_VERIFIED",
                severity: AuditSeverity.Sensitive,
                actorId: "system",
                targetEntity: "MSign",
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                cancellationToken: ct).ConfigureAwait(false);
        }

        return Result<SignatureVerificationResult>.Success(verification);
    }

    /// <summary>
    /// Pure, synchronous, exception-safe verification kernel. Parses the PKCS#7 envelope,
    /// builds an <see cref="X509Chain"/> with <see cref="MSignVerifyOptions.TrustedRoots"/>
    /// as the explicit trust anchor, and translates the chain status flags into the
    /// shape of <see cref="SignatureVerificationResult"/>. Any unexpected exception
    /// during parsing or chain-build is caught and surfaced as a populated invalid
    /// result — the API contract states verification failure is an OUTCOME, not an
    /// exception.
    /// </summary>
    /// <param name="signedPayload">DER-encoded PKCS#7 SignedData envelope.</param>
    /// <param name="options">Operator-supplied verification policy.</param>
    private SignatureVerificationResult PerformVerification(
        byte[] signedPayload, MSignVerifyOptions options)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(signedPayload);

            cms.CheckSignature(verifySignatureOnly: true);

            var signerCert = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
            if (signerCert is null)
            {
                return new SignatureVerificationResult(
                    IsValid: false,
                    SubjectCn: string.Empty,
                    IssuerCn: string.Empty,
                    NotBefore: DateTime.MinValue,
                    NotAfter: DateTime.MinValue,
                    SerialNumber: string.Empty,
                    ChainTrusted: false,
                    NotExpired: false,
                    NotRevoked: !options.RequireRevocationCheck,
                    RevocationCheckSkipped: !options.RequireRevocationCheck,
                    ValidationErrors: SignerMissingErrors);
            }

            var subjectCn = ExtractCn(signerCert.Subject);
            var issuerCn = ExtractCn(signerCert.Issuer);
            var notBefore = signerCert.NotBefore.ToUniversalTime();
            var notAfter = signerCert.NotAfter.ToUniversalTime();
            var serialNumber = signerCert.SerialNumber ?? string.Empty;
            var revocationSkipped = !options.RequireRevocationCheck;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            foreach (var root in options.TrustedRoots)
            {
                chain.ChainPolicy.CustomTrustStore.Add(root);
            }
            chain.ChainPolicy.RevocationMode = options.RequireRevocationCheck
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            // Reference the verification instant from the clock so tests can drive expiry.
            chain.ChainPolicy.VerificationTime = _clock.UtcNow;

            // Provide any extra certs bundled into the PKCS#7 envelope to the chain
            // builder so intermediate certs resolve without an outbound AIA fetch.
            foreach (var extra in cms.Certificates)
            {
                chain.ChainPolicy.ExtraStore.Add(extra);
            }

            var chainBuilt = chain.Build(signerCert);

            var errors = new List<string>();
            var chainTrusted = chainBuilt;
            var notExpired = true;
            var notRevoked = revocationSkipped;

            foreach (var status in chain.ChainStatus)
            {
                switch (status.Status)
                {
                    case X509ChainStatusFlags.NotTimeValid:
                    case X509ChainStatusFlags.CtlNotTimeValid:
                    case X509ChainStatusFlags.NotTimeNested:
                        notExpired = false;
                        errors.Add("NotExpired: " + status.StatusInformation.Trim());
                        chainTrusted = false;
                        break;
                    case X509ChainStatusFlags.Revoked:
                    case X509ChainStatusFlags.OfflineRevocation:
                        if (options.RequireRevocationCheck)
                        {
                            notRevoked = false;
                            errors.Add("Revoked: " + status.StatusInformation.Trim());
                            chainTrusted = false;
                        }
                        break;
                    case X509ChainStatusFlags.RevocationStatusUnknown:
                        // Only an error when revocation is required by the operator.
                        if (options.RequireRevocationCheck)
                        {
                            notRevoked = false;
                            errors.Add("RevocationStatusUnknown: " + status.StatusInformation.Trim());
                            chainTrusted = false;
                        }
                        break;
                    case X509ChainStatusFlags.NoError:
                        break;
                    default:
                        chainTrusted = false;
                        errors.Add(status.Status + ": " + status.StatusInformation.Trim());
                        break;
                }
            }

            // Defense-in-depth — if expiry not surfaced by ChainStatus, double-check
            // against the verification instant directly.
            var now = _clock.UtcNow;
            if (notExpired && (now < notBefore || now > notAfter))
            {
                notExpired = false;
                errors.Add("NotExpired: certificate not within validity window.");
                chainTrusted = false;
            }

            if (!revocationSkipped && notRevoked && options.RequireRevocationCheck)
            {
                // No revocation evidence found — notRevoked already true.
            }

            if (options.RequireTimestamp)
            {
                var hasTimestamp = SignedCmsHasRfc3161Timestamp(cms);
                if (!hasTimestamp)
                {
                    errors.Add("MissingTimestamp: signature does not carry an RFC 3161 timestamp.");
                    chainTrusted = false;
                }
            }

            var isValid = chainTrusted && notExpired && notRevoked && errors.Count == 0;
            return new SignatureVerificationResult(
                IsValid: isValid,
                SubjectCn: subjectCn,
                IssuerCn: issuerCn,
                NotBefore: notBefore,
                NotAfter: notAfter,
                SerialNumber: serialNumber,
                ChainTrusted: chainTrusted,
                NotExpired: notExpired,
                NotRevoked: notRevoked,
                RevocationCheckSkipped: revocationSkipped,
                ValidationErrors: errors);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException)
        {
            // Signature-verification failure is an outcome, not an exception. Surface as
            // an invalid result carrying the parse error in ValidationErrors so callers
            // can audit / display it without a try/catch dance.
            return new SignatureVerificationResult(
                IsValid: false,
                SubjectCn: string.Empty,
                IssuerCn: string.Empty,
                NotBefore: DateTime.MinValue,
                NotAfter: DateTime.MinValue,
                SerialNumber: string.Empty,
                ChainTrusted: false,
                NotExpired: false,
                NotRevoked: !options.RequireRevocationCheck,
                RevocationCheckSkipped: !options.RequireRevocationCheck,
                ValidationErrors: new List<string> { ex.GetType().Name + ": " + ex.Message });
        }
    }

    /// <summary>
    /// Extracts the value of the <c>CN=</c> attribute from a DN string. Returns the raw
    /// DN when no CN attribute is present so the caller always sees something useful.
    /// </summary>
    /// <param name="distinguishedName">An RFC 2253 distinguished name (e.g. <c>CN=Ion Popescu, O=CNAS, C=MD</c>).</param>
    private static string ExtractCn(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return string.Empty;
        }
        var parts = distinguishedName.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..].Trim();
            }
        }
        return distinguishedName;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied <see cref="SignedCms"/> envelope carries
    /// at least one RFC 3161 timestamp inside the first signer's unsigned-attribute
    /// collection. The standard OID for a signature-timestamp attribute is
    /// <c>1.2.840.113549.1.9.16.2.14</c> (id-aa-timeStampToken).
    /// </summary>
    /// <param name="cms">Decoded PKCS#7 envelope.</param>
    private static bool SignedCmsHasRfc3161Timestamp(SignedCms cms)
    {
        if (cms.SignerInfos.Count == 0)
        {
            return false;
        }
        const string TimeStampTokenOid = "1.2.840.113549.1.9.16.2.14";
        foreach (var attr in cms.SignerInfos[0].UnsignedAttributes)
        {
            if (attr.Oid?.Value == TimeStampTokenOid)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Posts <paramref name="envelopeBody"/> to <c>{MSignBaseUrl}/MSign.svc</c> wrapped
    /// in a SOAP 1.1 envelope, with <c>SOAPAction</c> set to
    /// <c>"http://egov.md/MSign/{operation}"</c>. Returns the parsed SOAP body element
    /// (the inner XML inside <c>&lt;soap:Body&gt;</c>) on a 2xx response, or a failed
    /// <see cref="Result"/> on transport / non-2xx / SOAP fault.
    /// </summary>
    /// <param name="operation">Operation name (e.g. <c>PostSignRequest</c>); used for the SOAPAction header.</param>
    /// <param name="envelopeBody">The inner XML to wrap inside <c>&lt;soap:Body&gt;</c>.</param>
    /// <param name="correlationId">Per-call correlation id sent as <c>X-Correlation-Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result<XElement>> SendSoapAsync(
        string operation,
        string envelopeBody,
        string correlationId,
        CancellationToken ct)
    {
        var fullEnvelope = WrapEnvelope(envelopeBody);
        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MSignBaseUrl.TrimEnd('/')}/MSign.svc")
            {
                Content = new StringContent(fullEnvelope, Encoding.UTF8, "text/xml"),
            };
            // mTLS replaces bearer auth — pass empty bearer so MGovHttp.Decorate omits the header.
            MGovHttp.Decorate(http, string.Empty, correlationId, _clock);
            // Override Accept — SOAP services return XML, not JSON.
            http.Headers.Accept.Clear();
            http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
            // SOAPAction header — quoted per WS-I Basic Profile 1.1 §R2744.
            http.Headers.TryAddWithoutValidation("SOAPAction", $"\"{MSignContractNs}/{operation}\"");

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            var responseText = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var faultString = TryExtractFaultString(responseText);
                _logger.LogWarning("MSign {Operation} call failed with status {Status} (correlation {Correlation}).",
                    operation, (int)response.StatusCode, correlationId);
                var message = faultString is null
                    ? "Upstream MSign call failed."
                    : "Upstream MSign call failed: " + faultString;
                return Result<XElement>.Failure(ErrorCodes.MSignFailed, message);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _logger.LogWarning("MSign {Operation} call returned an empty body (correlation {Correlation}).", operation, correlationId);
                return Result<XElement>.Failure(ErrorCodes.MSignFailed, "MSign returned an empty response.");
            }

            // Even with a 2xx, MSign may still return a SOAP fault; parse and check.
            var doc = XDocument.Parse(responseText, LoadOptions.None);
            var bodyEl = doc.Root?
                .Element(XName.Get("Body", SoapEnvelopeNs));
            if (bodyEl is null)
            {
                return Result<XElement>.Failure(ErrorCodes.MSignFailed, "MSign returned a non-SOAP response.");
            }
            var fault = bodyEl.Element(XName.Get("Fault", SoapEnvelopeNs));
            if (fault is not null)
            {
                var faultString = fault.Element("faultstring")?.Value
                    ?? fault.Element(XName.Get("faultstring", SoapEnvelopeNs))?.Value
                    ?? "(unspecified SOAP fault)";
                return Result<XElement>.Failure(ErrorCodes.MSignFailed, "Upstream MSign call failed: " + faultString);
            }
            return Result<XElement>.Success(bodyEl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MSign {Operation} transport failure (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MSignFailed, "Upstream MSign call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MSign {Operation} timed out (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MSignFailed, "Upstream MSign call failed.");
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "MSign {Operation} returned malformed XML (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MSignFailed, "Upstream MSign call failed.");
        }
    }

    /// <summary>
    /// Wraps a body fragment in a SOAP 1.1 envelope. The body fragment is the XML
    /// representation of the operation's request element (e.g.
    /// <c>&lt;PostSignRequest xmlns="http://egov.md/MSign"&gt;...&lt;/PostSignRequest&gt;</c>).
    /// </summary>
    /// <param name="bodyFragment">The inner XML to embed inside <c>soap:Body</c>.</param>
    private static string WrapEnvelope(string bodyFragment)
    {
        var sb = new StringBuilder(bodyFragment.Length + 256);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"");
        sb.Append(SoapEnvelopeNs);
        sb.Append("\"><s:Body>");
        sb.Append(bodyFragment);
        sb.Append("</s:Body></s:Envelope>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>&lt;PostSignRequest&gt;</c> body fragment. The document bytes are
    /// emitted as base64 inside <c>&lt;ContentBase64&gt;</c> — base64 is XML-safe
    /// (alphanumerics + <c>+/=</c>) so no further escaping is needed.
    /// </summary>
    private static string BuildPostSignRequestBody(MSignPostSignRequest request)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement("PostSignRequest", MSignContractNs);
            writer.WriteElementString("DocumentName", request.DocumentName);
            writer.WriteElementString("ContentType", request.ContentType);
            writer.WriteElementString("ContentMode", request.Mode.ToString());
            writer.WriteElementString("ContentBase64", Convert.ToBase64String(request.DocumentBytes));
            writer.WriteElementString("ReturnUrl", request.ReturnUrl.AbsoluteUri);
            writer.WriteEndElement();
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Builds a body fragment shaped like <c>&lt;OperationName&gt;&lt;RequestId&gt;...&lt;/RequestId&gt;&lt;/OperationName&gt;</c>.
    /// Used by the two single-arg operations <c>GetSignResponse</c> and <c>IsRequestReady</c>.
    /// </summary>
    private static string BuildSingleRequestIdBody(string operationName, string requestId)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement(operationName, MSignContractNs);
            writer.WriteElementString("RequestId", requestId);
            writer.WriteEndElement();
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Parses the <c>&lt;PostSignRequestResponse&gt;</c> element returned by MSign.
    /// </summary>
    private static MSignPostSignResult ParsePostSignResponse(XElement body)
    {
        // Locate the <PostSignRequestResult> element regardless of namespace prefix.
        var resultEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PostSignRequestResult")
            ?? throw new InvalidOperationException("PostSignRequestResult element missing.");

        var requestId = FindChildValue(resultEl, "RequestId")
            ?? throw new InvalidOperationException("RequestId missing.");
        var redirectUrl = FindChildValue(resultEl, "RedirectUrl")
            ?? throw new InvalidOperationException("RedirectUrl missing.");
        return new MSignPostSignResult(requestId, new Uri(redirectUrl, UriKind.Absolute));
    }

    /// <summary>Parses the <c>&lt;GetSignResponseResponse&gt;</c> element returned by MSign.</summary>
    private static MSignGetSignResult ParseGetSignResponse(XElement body)
    {
        var resultEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "GetSignResponseResult")
            ?? throw new InvalidOperationException("GetSignResponseResult element missing.");

        var sigBase64 = FindChildValue(resultEl, "SignatureBase64")
            ?? throw new InvalidOperationException("SignatureBase64 missing.");
        var signedAtRaw = FindChildValue(resultEl, "SignedAtUtc")
            ?? throw new InvalidOperationException("SignedAtUtc missing.");
        var signerIdnp = FindChildValue(resultEl, "SignerIdnp")
            ?? throw new InvalidOperationException("SignerIdnp missing.");
        var signerFullName = FindChildValue(resultEl, "SignerFullName");
        var thumbprint = FindChildValue(resultEl, "CertificateThumbprint");

        var signedAt = DateTime.Parse(
            signedAtRaw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var sigBytes = Convert.FromBase64String(sigBase64);
        return new MSignGetSignResult(
            SignatureBytes: sigBytes,
            Metadata: new MSignSignatureMetadata(signedAt, signerIdnp, signerFullName, thumbprint));
    }

    /// <summary>Parses the <c>&lt;IsRequestReadyResponse&gt;</c> element returned by MSign.</summary>
    private static bool ParseIsRequestReadyResponse(XElement body)
    {
        var resultEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "IsRequestReadyResult")
            ?? throw new InvalidOperationException("IsRequestReadyResult element missing.");
        var ready = FindChildValue(resultEl, "Ready")
            ?? throw new InvalidOperationException("Ready missing.");
        return bool.Parse(ready);
    }

    /// <summary>
    /// Locates a direct or descendant child element by local name (ignores namespace).
    /// Returns its text content, or <c>null</c> when the element is absent.
    /// </summary>
    private static string? FindChildValue(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Best-effort extractor for the SOAP fault string inside an error response body.
    /// Returns <c>null</c> when the body is not parseable or doesn't contain a fault.
    /// </summary>
    private static string? TryExtractFaultString(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }
        try
        {
            var doc = XDocument.Parse(responseText, LoadOptions.None);
            var fault = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is null)
            {
                return null;
            }
            return fault.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
