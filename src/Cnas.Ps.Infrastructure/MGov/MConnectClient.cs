using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// SOAP / WS-Security adapter for MConnect — the government interoperability bus that
/// routes calls into individual external systems (RSP, RSUD, SFS, CETAT, ...). Speaks
/// a hand-rolled WS-I Basic Profile envelope (SOAP) over mTLS — the X.509 client
/// certificate is the identity; no <c>Authorization</c> header is ever sent. The
/// production protocol additionally embeds an X.509 XML-DSig signature inside a
/// <c>wsse:Security</c> header — this adapter reserves the header element with an
/// empty placeholder so the signing step can be wired in once the per-system MConnect
/// contracts are obtained from MEGA (NDA-gated).
/// </summary>
/// <remarks>
/// <para>
/// The 11 typed facades (<c>IRspClient</c>, <c>IRsudClient</c>, <c>ISfsClient</c>, ...) sit
/// on top of this transport and keep their domain shapes. They pass a stable
/// <c>serviceCode</c> (e.g. <c>RSP.GetPerson</c>) plus a JSON request body; this client
/// wraps both in a SOAP envelope and posts to <c>POST {MConnectBaseUrl}/MConnect.svc</c>
/// with <c>SOAPAction: "http://egov.md/MConnect/Call"</c>. The response is a SOAP
/// envelope carrying <c>&lt;CallResponse&gt;&lt;ResponseJson&gt;{json}&lt;/ResponseJson&gt;&lt;/CallResponse&gt;</c>;
/// the inner JSON string is returned to the facade verbatim so it can deserialise with
/// the schema appropriate to its service.
/// </para>
/// <para>
/// SOAP envelopes are hand-rolled with <see cref="XmlWriter"/> rather than the
/// auto-generated <c>System.ServiceModel</c> proxy because the official per-system
/// MConnect WSDLs are NDA-gated and not publicly distributable. Hand-rolling also keeps
/// the dependency surface minimal and lets the adapter interop cleanly with the
/// SOAP-1.2 envelope shape used by the real MConnect gateway.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed-client; the primary handler attaches the mTLS cert.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; bodies are NOT logged (third-party PII).</param>
/// <param name="clock">UTC clock — used for the <c>X-Request-Date</c> header.</param>
/// <param name="auditService">
/// R0104 — optional audit sink. When supplied, the fallback path emits a Notice
/// <c>MCONNECT.FALLBACK_INVOKED</c> row. <c>null</c>-safe so legacy compositions that
/// pre-date the fallback feature keep compiling.
/// </param>
public sealed class MConnectClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MConnectClient> logger,
    ICnasTimeProvider clock,
    IAuditService? auditService = null) : IMConnectClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MConnectClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService? _auditService = auditService;

    /// <summary>
    /// SOAP envelope namespace. MConnect uses SOAP 1.2
    /// (<c>http://www.w3.org/2003/05/soap-envelope</c>) per the AGE gateway
    /// convention — distinct from the SOAP 1.1 namespace used by MSign / MPay.
    /// </summary>
    private const string SoapEnvelopeNs = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>
    /// WS-Security 2004 namespace. Used for the empty <c>wsse:Security</c> header
    /// reserved in the outbound envelope; once the per-system contracts are in hand the
    /// X.509 XML-DSig signature lands inside this element.
    /// </summary>
    private const string WSSecurityNs =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    /// <summary>
    /// Service-contract namespace used by MConnect for the <c>Call</c> request /
    /// <c>CallResponse</c> response wrapper elements.
    /// </summary>
    private const string MConnectContractNs = "http://egov.md/MConnect";

    /// <inheritdoc />
    public Task<Result<string>> CallAsync(string serviceCode, string requestJson, CancellationToken cancellationToken = default)
        => CallAsync(serviceCode, requestJson, fallback: null, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<string>> CallAsync(
        string serviceCode,
        string requestJson,
        MConnectFallback? fallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceCode);
        ArgumentNullException.ThrowIfNull(requestJson);

        if (string.IsNullOrWhiteSpace(_options.MConnectBaseUrl))
        {
            _logger.LogWarning("MConnect called without configured base URL — returning MCONNECT_FAILED.");
            return Result<string>.Failure(ErrorCodes.MConnectFailed, "BaseUrl not configured");
        }

        var body = BuildCallBody(serviceCode, requestJson);
        // Correlation id derived from serviceCode + body so retries hit the same upstream row.
        var correlationId = MGovHttp.DeriveCorrelationId($"{serviceCode}\n{requestJson}");

        var soapResult = await SendSoapAsync(
            envelopeBody: body,
            correlationId: correlationId,
            serviceCode: serviceCode,
            ct: cancellationToken).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            // R0104 — fallback eligibility. Only availability failures (timeout / HTTP 5xx /
            // network) qualify; 4xx outcomes are partner business logic that the direct call
            // would reproduce. The unavailability reason is carried in soapResult.FailureReason
            // when set; null means "non-fallback-eligible" (4xx / malformed envelope / etc).
            if (fallback is not null
                && soapResult.FailureReason is not null
                && fallback.PartnerHasNda
                && _options.AllowFallback)
            {
                return await InvokeFallbackAsync(
                    fallback,
                    soapResult.FailureReason.Value,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }
            return Result<string>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var responseJson = ParseCallResponse(soapResult.Value!);
            return Result<string>.Success(responseJson);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "MConnect Call returned an unparseable SOAP body for {Service} (correlation {Correlation}).",
                serviceCode, correlationId);
            return Result<string>.Failure(ErrorCodes.MConnectFailed, "MConnect returned a malformed Call response.");
        }
    }

    /// <summary>
    /// R0104 fallback driver. Increments the <c>cnas.mconnect.fallback_invoked</c> counter
    /// (tagged with partner + reason), emits a Notice audit row, and dispatches the
    /// supplied direct closure. On closure failure (returns a failed Result OR throws),
    /// translates to <c>MCONNECT_FALLBACK_FAILED</c> and increments the
    /// <c>cnas.mconnect.fallback_failed</c> counter so operators can chart fallback
    /// reliability independently of MConnect itself.
    /// </summary>
    /// <param name="fallback">Fallback descriptor supplied by the typed facade.</param>
    /// <param name="reason">Classified MConnect availability-failure reason.</param>
    /// <param name="correlationId">Per-call correlation id for audit attribution.</param>
    /// <param name="ct">Cancellation token forwarded to the closure.</param>
    private async Task<Result<string>> InvokeFallbackAsync(
        MConnectFallback fallback,
        MConnectFailureReason reason,
        string correlationId,
        CancellationToken ct)
    {
        var partner = fallback.PartnerSystemCode;
        CnasMeter.MConnectFallbackInvoked.Add(
            1,
            new KeyValuePair<string, object?>("partner", partner),
            new KeyValuePair<string, object?>("reason", reason.ToString()));

        if (_auditService is not null)
        {
            var details = JsonSerializer.Serialize(new
            {
                partner,
                reason = reason.ToString(),
            });
            _ = await _auditService.RecordAsync(
                eventCode: "MCONNECT.FALLBACK_INVOKED",
                severity: AuditSeverity.Notice,
                actorId: "system",
                targetEntity: "MConnect",
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: correlationId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        try
        {
            var direct = await fallback.DirectInvoke(ct).ConfigureAwait(false);
            if (direct.IsSuccess)
            {
                return direct;
            }
            _logger.LogWarning(
                "MConnect partner-direct fallback for {Partner} failed: {Code} {Message} (correlation {Correlation}).",
                partner, direct.ErrorCode, direct.ErrorMessage, correlationId);
            CnasMeter.MConnectFallbackFailed.Add(
                1,
                new KeyValuePair<string, object?>("partner", partner));
            return Result<string>.Failure(
                ErrorCodes.MConnectFallbackFailed,
                $"Partner-direct fallback for {partner} failed: {direct.ErrorMessage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "MConnect partner-direct fallback for {Partner} threw (correlation {Correlation}).",
                partner, correlationId);
            CnasMeter.MConnectFallbackFailed.Add(
                1,
                new KeyValuePair<string, object?>("partner", partner));
            return Result<string>.Failure(
                ErrorCodes.MConnectFallbackFailed,
                $"Partner-direct fallback for {partner} threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Classification of MConnect availability failures. Only failures whose reason is
    /// <em>set</em> (non-null) qualify for the R0104 partner-direct fallback path —
    /// partner business outcomes (404, 422, ...) deliberately surface as <c>null</c>
    /// so the fallback never papers over upstream business logic. R0104.
    /// </summary>
    private enum MConnectFailureReason
    {
        /// <summary>The SOAP call exceeded the configured timeout / cancellation.</summary>
        Timeout,

        /// <summary>The upstream returned an HTTP 5xx response.</summary>
        Http5xx,

        /// <summary>A transport-layer exception (connection refused, DNS failure, ...) occurred.</summary>
        Network,
    }

    /// <summary>
    /// Internal result wrapper carrying both the parsed SOAP body (on success) AND the
    /// classified availability-failure reason (on failure) so the
    /// <see cref="CallAsync(string, string, MConnectFallback?, CancellationToken)"/> overload
    /// can decide whether to invoke the R0104 fallback. The <see cref="FailureReason"/>
    /// is deliberately <c>null</c> for non-availability failures (4xx business outcomes,
    /// SOAP faults, malformed envelopes) so those NEVER trigger fallback.
    /// </summary>
    /// <param name="IsSuccess">Whether the SOAP body parsed cleanly.</param>
    /// <param name="Value">Parsed body element on success; <c>null</c> on failure.</param>
    /// <param name="ErrorCode">Error code on failure; <c>null</c> on success.</param>
    /// <param name="ErrorMessage">Error message on failure; <c>null</c> on success.</param>
    /// <param name="FailureReason">
    /// Classified availability-failure reason. <c>null</c> for non-availability failures
    /// (4xx, SOAP fault, malformed XML) so the fallback path stays clear of them.
    /// </param>
    private readonly record struct SoapCallOutcome(
        bool IsSuccess,
        XElement? Value,
        string? ErrorCode,
        string? ErrorMessage,
        MConnectFailureReason? FailureReason)
    {
        /// <summary>Whether the call failed (mirror of <see cref="IsSuccess"/>).</summary>
        public bool IsFailure => !IsSuccess;

        /// <summary>Success constructor.</summary>
        public static SoapCallOutcome Success(XElement value) =>
            new(true, value, null, null, null);

        /// <summary>Failure constructor.</summary>
        public static SoapCallOutcome Failure(string errorCode, string errorMessage, MConnectFailureReason? reason) =>
            new(false, null, errorCode, errorMessage, reason);
    }

    /// <summary>
    /// Posts <paramref name="envelopeBody"/> to <c>{MConnectBaseUrl}/MConnect.svc</c>
    /// wrapped in a SOAP envelope, with <c>SOAPAction</c> set to
    /// <c>"http://egov.md/MConnect/Call"</c>. Returns the parsed SOAP body element
    /// (the inner XML inside <c>&lt;soap:Body&gt;</c>) on a 2xx response, or a failed
    /// <see cref="SoapCallOutcome"/> on transport / non-2xx / SOAP fault / malformed XML.
    /// The outcome carries an availability-failure reason (timeout / 5xx / network) when
    /// the failure is fallback-eligible; non-eligible failures (4xx, malformed envelope)
    /// carry <c>null</c>.
    /// </summary>
    /// <param name="envelopeBody">The inner XML to wrap inside <c>&lt;soap:Body&gt;</c>.</param>
    /// <param name="correlationId">Per-call correlation id sent as <c>X-Correlation-Id</c>.</param>
    /// <param name="serviceCode">Service code under call — used for log attribution only.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<SoapCallOutcome> SendSoapAsync(
        string envelopeBody,
        string correlationId,
        string serviceCode,
        CancellationToken ct)
    {
        var fullEnvelope = WrapEnvelope(envelopeBody);
        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MConnectBaseUrl.TrimEnd('/')}/MConnect.svc")
            {
                Content = new StringContent(fullEnvelope, Encoding.UTF8, "text/xml"),
            };
            // mTLS replaces bearer auth — pass empty bearer so MGovHttp.Decorate omits the header.
            MGovHttp.Decorate(http, string.Empty, correlationId, _clock);
            // Override Accept — SOAP services return XML, not JSON.
            http.Headers.Accept.Clear();
            http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
            // SOAPAction header — quoted per WS-I Basic Profile 1.1 §R2744.
            http.Headers.TryAddWithoutValidation("SOAPAction", $"\"{MConnectContractNs}/Call\"");

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            var responseText = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var faultString = TryExtractFaultString(responseText);
                _logger.LogWarning("MConnect Call for {Service} failed with status {Status} (correlation {Correlation}).",
                    serviceCode, (int)response.StatusCode, correlationId);
                var message = faultString is null
                    ? "Upstream MConnect call failed."
                    : "Upstream MConnect call failed: " + faultString;
                // Only HTTP 5xx is an availability failure; 4xx is partner business logic
                // and must NOT trigger the R0104 fallback (the direct call would reproduce
                // the same business outcome).
                var statusCode = (int)response.StatusCode;
                var reason = statusCode >= 500 && statusCode <= 599
                    ? (MConnectFailureReason?)MConnectFailureReason.Http5xx
                    : null;
                return SoapCallOutcome.Failure(ErrorCodes.MConnectFailed, message, reason);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _logger.LogWarning("MConnect Call for {Service} returned an empty body (correlation {Correlation}).",
                    serviceCode, correlationId);
                return SoapCallOutcome.Failure(ErrorCodes.MConnectFailed, "MConnect returned an empty response.", null);
            }

            // Even with a 2xx, MConnect may still return a SOAP fault; parse and check.
            var doc = XDocument.Parse(responseText, LoadOptions.None);
            var bodyEl = doc.Root?
                .Element(XName.Get("Body", SoapEnvelopeNs));
            if (bodyEl is null)
            {
                return SoapCallOutcome.Failure(ErrorCodes.MConnectFailed, "MConnect returned a non-SOAP response.", null);
            }
            var fault = bodyEl.Element(XName.Get("Fault", SoapEnvelopeNs));
            if (fault is not null)
            {
                var faultString = fault.Element("faultstring")?.Value
                    ?? fault.Element(XName.Get("faultstring", SoapEnvelopeNs))?.Value
                    // SOAP 1.2 fault carries human-readable text under <Reason><Text>.
                    ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                    ?? "(unspecified SOAP fault)";
                return SoapCallOutcome.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect call failed: " + faultString, null);
            }
            return SoapCallOutcome.Success(bodyEl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MConnect transport failure for {Service} (correlation {Correlation}).",
                serviceCode, correlationId);
            return SoapCallOutcome.Failure(
                ErrorCodes.MConnectFailed, "Upstream MConnect call failed.", MConnectFailureReason.Network);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MConnect timed out for {Service} (correlation {Correlation}).",
                serviceCode, correlationId);
            return SoapCallOutcome.Failure(
                ErrorCodes.MConnectFailed, "Upstream MConnect call failed.", MConnectFailureReason.Timeout);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "MConnect returned malformed XML for {Service} (correlation {Correlation}).",
                serviceCode, correlationId);
            return SoapCallOutcome.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect call failed.", null);
        }
    }

    /// <summary>
    /// Wraps a body fragment in a SOAP 1.2 envelope. The envelope reserves a
    /// <c>wsse:Security</c> header element so the production X.509 XML-DSig signature
    /// (see the inline TODO comment) can be inserted once the per-system MConnect
    /// contracts are obtained from MEGA. Until then the header is empty but
    /// structurally present, matching the "structure in place but per-system WSDL not
    /// yet available" stance documented in
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MConnect".
    /// </summary>
    /// <param name="bodyFragment">The inner XML to embed inside <c>soap:Body</c>.</param>
    private static string WrapEnvelope(string bodyFragment)
    {
        var sb = new StringBuilder(bodyFragment.Length + 512);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<soap:Envelope xmlns:soap=\"");
        sb.Append(SoapEnvelopeNs);
        sb.Append("\" xmlns:wsse=\"");
        sb.Append(WSSecurityNs);
        sb.Append("\">");
        sb.Append("<soap:Header>");
        // TODO[mconnect-wss]: insert X509 XML-DSig signature when the per-system
        // MConnect contract is obtained from MEGA.
        sb.Append("<wsse:Security soap:mustUnderstand=\"1\">");
        sb.Append("<!-- TODO[mconnect-wss]: insert X509 XML-DSig signature when the per-system MConnect contract is obtained from MEGA -->");
        sb.Append("</wsse:Security>");
        sb.Append("</soap:Header>");
        sb.Append("<soap:Body>");
        sb.Append(bodyFragment);
        sb.Append("</soap:Body></soap:Envelope>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>&lt;Call&gt;</c> body fragment carrying the upstream service code
    /// and the caller's raw JSON request payload. The JSON is wrapped in a CDATA
    /// section so embedded <c>&lt;</c>, <c>&gt;</c>, and <c>&amp;</c> characters
    /// (legal in JSON string values) do not corrupt the surrounding XML — the per-
    /// system schemas are owned by external systems and we never re-encode them.
    /// </summary>
    /// <param name="serviceCode">Upstream service code (e.g. <c>RSP.GetPerson</c>); forwarded verbatim.</param>
    /// <param name="requestJson">The caller's JSON body; embedded as CDATA so XML reserved characters survive.</param>
    private static string BuildCallBody(string serviceCode, string requestJson)
    {
        // Use XmlWriter for the wrapping element so namespace declarations and element
        // names are produced canonically; emit the CDATA section directly so the JSON
        // payload bypasses XmlWriter's text-escaping (which would otherwise convert
        // legal-in-JSON characters into entity references).
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement("Call", MConnectContractNs);
            writer.WriteElementString("ServiceCode", serviceCode);
            writer.WriteStartElement("RequestJson");
            writer.WriteCData(requestJson);
            writer.WriteEndElement(); // </RequestJson>
            writer.WriteEndElement(); // </Call>
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Parses the <c>&lt;CallResponse&gt;&lt;ResponseJson&gt;...&lt;/ResponseJson&gt;&lt;/CallResponse&gt;</c>
    /// element returned by MConnect and returns the inner JSON string verbatim. The
    /// XML reader has already decoded any CDATA / entity references so the caller
    /// sees the canonical JSON payload as it was on the upstream side.
    /// </summary>
    /// <param name="body">The <c>&lt;soap:Body&gt;</c> XElement.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the SOAP body does not contain the expected
    /// <c>&lt;CallResponse&gt;</c> / <c>&lt;ResponseJson&gt;</c> wrapper elements.
    /// </exception>
    private static string ParseCallResponse(XElement body)
    {
        var responseEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "CallResponse")
            ?? throw new InvalidOperationException("CallResponse element missing.");

        var jsonEl = responseEl
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ResponseJson")
            ?? throw new InvalidOperationException("ResponseJson element missing.");

        return jsonEl.Value;
    }

    /// <summary>
    /// Best-effort extractor for the SOAP fault string inside an error response body.
    /// Handles both SOAP 1.1 (<c>&lt;faultstring&gt;</c>) and SOAP 1.2
    /// (<c>&lt;Reason&gt;&lt;Text&gt;</c>) fault shapes. Returns <c>null</c> when the
    /// body is not parseable or doesn't contain a fault.
    /// </summary>
    /// <param name="responseText">Raw response body, possibly XML.</param>
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
            // SOAP 1.1: <faultstring>
            var soap11 = fault.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
            if (!string.IsNullOrWhiteSpace(soap11))
            {
                return soap11;
            }
            // SOAP 1.2: <Reason><Text>...</Text></Reason>
            return fault.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text")?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
