using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// SOAP / WS-Security adapter for MPay — the government electronic-payments platform.
/// Speaks a hand-rolled WS-I Basic Profile 1.1 envelope (SOAP 1.1) over mTLS — the
/// X.509 client certificate is the identity; no <c>Authorization</c> header is ever
/// sent. The production protocol additionally embeds an X.509 XML-DSig signature inside
/// a <c>wsse:Security</c> header — this adapter reserves the header element with an
/// empty placeholder body so the signing step can be wired in once the upstream WSDL is
/// available (NDA-gated, request from <c>suport.mpay@gov.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The real protocol is a three-operation surface:
/// </para>
/// <list type="number">
///   <item>
///     <see cref="PostOrderAsync"/> — server-to-server SOAP call posting the order
///     descriptor + amount + return URL. MPay allocates an <c>MPayOrderId</c> and a
///     redirect URL. The CNAS UI then redirects the payer's browser to that URL.
///   </item>
///   <item>
///     The citizen completes payment in the MPay portal. MPay POSTs to the CNAS-side
///     callback endpoint (<c>POST /api/mpay/orders/{orderId}/confirm</c>) and
///     simultaneously redirects the browser back to the supplied <c>ReturnUrl</c>.
///   </item>
///   <item>
///     <see cref="GetOrderStatusAsync"/> — server-to-server SOAP call reading the
///     current state of an order. Used by reconciliation jobs and recovery flows.
///   </item>
/// </list>
/// <para>
/// The legacy <see cref="SendAsync"/> entry point is preserved as a back-compat shim
/// that internally calls <see cref="PostOrderAsync"/> and synthesises an
/// <see cref="MPayReceipt"/> from the posted order so existing call sites (chiefly
/// <c>MPayDispatcherJob</c>) keep compiling. New code should call
/// <see cref="PostOrderAsync"/> directly and thread the redirect URL through the UI.
/// </para>
/// <para>
/// SOAP envelopes are hand-rolled with <see cref="XmlWriter"/> rather than the
/// auto-generated <c>System.ServiceModel</c> proxy because the official MPay SOAP
/// client is NDA-gated and not publicly distributable. Hand-rolling also keeps the
/// dependency surface minimal and lets the adapter interop cleanly with the WS-I
/// envelope shape.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed-client; the primary handler attaches the mTLS cert.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; never receives the request/response body (financial PII).</param>
/// <param name="clock">UTC clock — used for the <c>X-Request-Date</c> header.</param>
/// <param name="orderStore">
/// Persistence façade — the client persists a pending <c>MPayOrder</c> row via
/// <see cref="IMPayOrderStore.CreateAsync"/> BEFORE the outbound SOAP send so the inbound
/// callback controller always finds a row to read and update. A failed
/// <see cref="IMPayOrderStore.CreateAsync"/> propagates as
/// <see cref="ErrorCodes.MPayFailed"/> — we never POST to MPay without a local record,
/// which keeps the "Idempotent Callbacks" invariant intact (CLAUDE.md cross-cutting).
/// </param>
public sealed class MPayClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MPayClient> logger,
    ICnasTimeProvider clock,
    IMPayOrderStore orderStore) : IMPayClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MPayClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IMPayOrderStore _orderStore = orderStore;

    /// <summary>SOAP envelope namespace (WS-I Basic Profile 1.1 — SOAP 1.1).</summary>
    private const string SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// WS-Security 2004 namespace. Used for the empty <c>wsse:Security</c> header
    /// reserved in the outbound envelope; once the WSDL is in hand the
    /// X.509 XML-DSig signature lands inside this element.
    /// </summary>
    private const string WSSecurityNs =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    /// <summary>
    /// Service-contract namespace used by MPay for operation request / response
    /// elements (e.g. <c>PostOrder</c>, <c>GetOrderStatus</c>, <c>CancelOrder</c>).
    /// </summary>
    private const string MPayContractNs = "http://egov.md/MPay";

    /// <inheritdoc />
    public async Task<Result<MPayPostOrderResult>> PostOrderAsync(
        MPayPostOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.MPayBaseUrl))
        {
            _logger.LogWarning("MPay PostOrder called without configured base URL — returning MPAY_FAILED.");
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.MPayFailed, "BaseUrl not configured");
        }
        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.ValidationFailed, "OrderId is required.");
        }
        if (string.IsNullOrWhiteSpace(request.CitizenIdnp))
        {
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.ValidationFailed, "CitizenIdnp is required.");
        }
        if (string.IsNullOrWhiteSpace(request.ServiceCode))
        {
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.ValidationFailed, "ServiceCode is required.");
        }
        if (request.ReturnUrl is null)
        {
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.ValidationFailed, "ReturnUrl is required.");
        }

        var body = BuildPostOrderBody(request);
        var correlationId = request.CorrelationId ?? MGovHttp.DeriveCorrelationId(body);

        // Persist a pending row BEFORE the outbound SOAP send so the inbound MPay
        // callbacks (GET .../details, POST .../confirm) always find a row to operate on.
        // A pre-existing row for the same OrderId (Conflict) is tolerated as a retry —
        // the upstream service may legitimately re-call PostOrderAsync on transport
        // failure; the inbound callback contract is already satisfied. Any OTHER store
        // failure halts the dispatch with ErrorCodes.MPayFailed so we never POST to MPay
        // without a corresponding local record.
        var persist = await _orderStore.CreateAsync(new MPayOrderSnapshot(
            OrderId: request.OrderId,
            AmountMdl: request.AmountMdl,
            DescriptionRo: request.DescriptionRo,
            BeneficiaryIdnp: request.CitizenIdnp,
            PaymentRef: null,
            ConfirmedAtUtc: null), ct).ConfigureAwait(false);
        if (persist.IsFailure && !string.Equals(persist.ErrorCode, ErrorCodes.Conflict, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "MPay PostOrder aborted because the local order row could not be persisted (correlation {Correlation}): {ErrorCode}.",
                correlationId, persist.ErrorCode);
            return Result<MPayPostOrderResult>.Failure(
                ErrorCodes.MPayFailed,
                "Failed to persist the MPay order row before dispatch.");
        }

        var soapResult = await SendSoapAsync(
            operation: "PostOrder",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result<MPayPostOrderResult>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var parsed = ParsePostOrderResponse(soapResult.Value);
            return Result<MPayPostOrderResult>.Success(parsed);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(ex, "MPay PostOrder returned an unparseable SOAP body (correlation {Correlation}).", correlationId);
            return Result<MPayPostOrderResult>.Failure(ErrorCodes.MPayFailed, "MPay returned a malformed PostOrder response.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MPayOrderStatus>> GetOrderStatusAsync(
        string orderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Result<MPayOrderStatus>.Failure(ErrorCodes.ValidationFailed, "OrderId is required.");
        }
        if (string.IsNullOrWhiteSpace(_options.MPayBaseUrl))
        {
            _logger.LogWarning("MPay GetOrderStatus called without configured base URL — returning MPAY_FAILED.");
            return Result<MPayOrderStatus>.Failure(ErrorCodes.MPayFailed, "BaseUrl not configured");
        }

        var body = BuildSingleOrderIdBody("GetOrderStatus", orderId);
        var correlationId = MGovHttp.DeriveCorrelationId(body);

        var soapResult = await SendSoapAsync(
            operation: "GetOrderStatus",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result<MPayOrderStatus>.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }

        try
        {
            var parsed = ParseGetOrderStatusResponse(soapResult.Value);
            return Result<MPayOrderStatus>.Success(parsed);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "MPay GetOrderStatus returned an unparseable SOAP body (correlation {Correlation}).", correlationId);
            return Result<MPayOrderStatus>.Failure(ErrorCodes.MPayFailed, "MPay returned a malformed GetOrderStatus response.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> CancelOrderAsync(
        string orderId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "OrderId is required.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Reason is required.");
        }
        if (string.IsNullOrWhiteSpace(_options.MPayBaseUrl))
        {
            _logger.LogWarning("MPay CancelOrder called without configured base URL — returning MPAY_FAILED.");
            return Result.Failure(ErrorCodes.MPayFailed, "BaseUrl not configured");
        }

        var body = BuildCancelOrderBody(orderId, reason);
        var correlationId = MGovHttp.DeriveCorrelationId(body);

        var soapResult = await SendSoapAsync(
            operation: "CancelOrder",
            envelopeBody: body,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
        if (soapResult.IsFailure)
        {
            return Result.Failure(soapResult.ErrorCode!, soapResult.ErrorMessage!);
        }
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<MPayReceipt>> SendAsync(MPayOutbound payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        // Legacy shim: translate the 4-tuple into a canonical PostOrder request. The
        // shim only drives the server-to-server step — the browser redirect is out of
        // scope because the legacy caller (MPayDispatcherJob) is a background job, not
        // a UI handler. The CNAS-side order id is the legacy reference verbatim so that
        // upstream MPay retries dedupe correctly.
        var post = await PostOrderAsync(new MPayPostOrderRequest(
            OrderId: payment.Reference,
            AmountMdl: payment.AmountMdl,
            CitizenIdnp: payment.BeneficiaryIdnp,
            ServiceCode: "CNAS.LEGACY",
            DescriptionRo: $"Plată CNAS — IBAN {payment.BeneficiaryIban}",
            ReturnUrl: new Uri("https://localhost/legacy-shim/no-redirect"),
            CorrelationId: null), cancellationToken).ConfigureAwait(false);
        if (post.IsFailure)
        {
            return Result<MPayReceipt>.Failure(post.ErrorCode!, post.ErrorMessage!);
        }

        // The legacy receipt echoes the MPay-allocated order id as the transaction id and
        // reports a "Pending" status because the citizen has not yet completed payment.
        return Result<MPayReceipt>.Success(new MPayReceipt(
            TransactionId: post.Value.MPayOrderId,
            Status: MPayOrderState.Pending.ToString()));
    }

    /// <summary>
    /// Posts <paramref name="envelopeBody"/> to <c>{MPayBaseUrl}/MPay.svc</c> wrapped
    /// in a SOAP 1.1 envelope, with <c>SOAPAction</c> set to
    /// <c>"http://egov.md/MPay/{operation}"</c>. Returns the parsed SOAP body element
    /// (the inner XML inside <c>&lt;soap:Body&gt;</c>) on a 2xx response, or a failed
    /// <see cref="Result"/> on transport / non-2xx / SOAP fault.
    /// </summary>
    /// <param name="operation">Operation name (e.g. <c>PostOrder</c>); used for the SOAPAction header.</param>
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
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MPayBaseUrl.TrimEnd('/')}/MPay.svc")
            {
                Content = new StringContent(fullEnvelope, Encoding.UTF8, "text/xml"),
            };
            // mTLS replaces bearer auth — pass empty bearer so MGovHttp.Decorate omits the header.
            MGovHttp.Decorate(http, string.Empty, correlationId, _clock);
            // Override Accept — SOAP services return XML, not JSON.
            http.Headers.Accept.Clear();
            http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
            // SOAPAction header — quoted per WS-I Basic Profile 1.1 §R2744.
            http.Headers.TryAddWithoutValidation("SOAPAction", $"\"{MPayContractNs}/{operation}\"");

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            var responseText = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var faultString = TryExtractFaultString(responseText);
                _logger.LogWarning("MPay {Operation} call failed with status {Status} (correlation {Correlation}).",
                    operation, (int)response.StatusCode, correlationId);
                var message = faultString is null
                    ? "Upstream MPay call failed."
                    : "Upstream MPay call failed: " + faultString;
                return Result<XElement>.Failure(ErrorCodes.MPayFailed, message);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _logger.LogWarning("MPay {Operation} call returned an empty body (correlation {Correlation}).", operation, correlationId);
                return Result<XElement>.Failure(ErrorCodes.MPayFailed, "MPay returned an empty response.");
            }

            // Even with a 2xx, MPay may still return a SOAP fault; parse and check.
            var doc = XDocument.Parse(responseText, LoadOptions.None);
            var bodyEl = doc.Root?
                .Element(XName.Get("Body", SoapEnvelopeNs));
            if (bodyEl is null)
            {
                return Result<XElement>.Failure(ErrorCodes.MPayFailed, "MPay returned a non-SOAP response.");
            }
            var fault = bodyEl.Element(XName.Get("Fault", SoapEnvelopeNs));
            if (fault is not null)
            {
                var faultString = fault.Element("faultstring")?.Value
                    ?? fault.Element(XName.Get("faultstring", SoapEnvelopeNs))?.Value
                    ?? "(unspecified SOAP fault)";
                return Result<XElement>.Failure(ErrorCodes.MPayFailed, "Upstream MPay call failed: " + faultString);
            }
            return Result<XElement>.Success(bodyEl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MPay {Operation} transport failure (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MPayFailed, "Upstream MPay call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MPay {Operation} timed out (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MPayFailed, "Upstream MPay call failed.");
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "MPay {Operation} returned malformed XML (correlation {Correlation}).", operation, correlationId);
            return Result<XElement>.Failure(ErrorCodes.MPayFailed, "Upstream MPay call failed.");
        }
    }

    /// <summary>
    /// Wraps a body fragment in a SOAP 1.1 envelope. The envelope reserves a
    /// <c>wsse:Security</c> header element so the production X.509 XML-DSig signature
    /// (see comment inside the header) can be inserted once the upstream WSDL is
    /// obtained from <c>suport.mpay@gov.md</c>. Until then the header is empty but
    /// structurally present, matching the "structure in place but WSDL not yet
    /// available" stance documented in <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MPay".
    /// </summary>
    /// <param name="bodyFragment">The inner XML to embed inside <c>soap:Body</c>.</param>
    private static string WrapEnvelope(string bodyFragment)
    {
        var sb = new StringBuilder(bodyFragment.Length + 512);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"");
        sb.Append(SoapEnvelopeNs);
        sb.Append("\" xmlns:wsse=\"");
        sb.Append(WSSecurityNs);
        sb.Append("\">");
        sb.Append("<s:Header>");
        // TODO[mpay-wss]: insert X509 XML-DSig signature when MPay WSDL is obtained
        sb.Append("<wsse:Security s:mustUnderstand=\"1\">");
        sb.Append("<!-- TODO[mpay-wss]: insert X509 XML-DSig signature when MPay WSDL is obtained -->");
        sb.Append("</wsse:Security>");
        sb.Append("</s:Header>");
        sb.Append("<s:Body>");
        sb.Append(bodyFragment);
        sb.Append("</s:Body></s:Envelope>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>&lt;PostOrder&gt;</c> body fragment carrying the order descriptor,
    /// amount, citizen IDNP, service code, Romanian description, and return URL.
    /// </summary>
    /// <param name="request">The canonical request payload.</param>
    private static string BuildPostOrderBody(MPayPostOrderRequest request)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement("PostOrder", MPayContractNs);
            writer.WriteElementString("OrderId", request.OrderId);
            writer.WriteElementString("AmountMdl", request.AmountMdl.ToString("0.00", CultureInfo.InvariantCulture));
            writer.WriteElementString("CitizenIdnp", request.CitizenIdnp);
            writer.WriteElementString("ServiceCode", request.ServiceCode);
            writer.WriteElementString("DescriptionRo", request.DescriptionRo);
            writer.WriteElementString("ReturnUrl", request.ReturnUrl.AbsoluteUri);
            writer.WriteEndElement();
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Builds a body fragment shaped like
    /// <c>&lt;OperationName&gt;&lt;OrderId&gt;...&lt;/OrderId&gt;&lt;/OperationName&gt;</c>.
    /// Used by <c>GetOrderStatus</c>.
    /// </summary>
    /// <param name="operationName">SOAP operation name — used as the wrapping element local name.</param>
    /// <param name="orderId">The CNAS-side order identifier.</param>
    private static string BuildSingleOrderIdBody(string operationName, string orderId)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement(operationName, MPayContractNs);
            writer.WriteElementString("OrderId", orderId);
            writer.WriteEndElement();
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Builds the <c>&lt;CancelOrder&gt;</c> body fragment carrying the order id plus
    /// the free-text rationale forwarded to MPay audit logs.
    /// </summary>
    /// <param name="orderId">The CNAS-side order identifier to cancel.</param>
    /// <param name="reason">Free-text rationale surfaced in MPay audit.</param>
    private static string BuildCancelOrderBody(string orderId, string reason)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteStartElement("CancelOrder", MPayContractNs);
            writer.WriteElementString("OrderId", orderId);
            writer.WriteElementString("Reason", reason);
            writer.WriteEndElement();
        }
        return stringWriter.ToString();
    }

    /// <summary>
    /// Parses the <c>&lt;PostOrderResponse&gt;</c> element returned by MPay.
    /// </summary>
    /// <param name="body">The <c>&lt;soap:Body&gt;</c> XElement.</param>
    private static MPayPostOrderResult ParsePostOrderResponse(XElement body)
    {
        var resultEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PostOrderResult")
            ?? throw new InvalidOperationException("PostOrderResult element missing.");

        var mpayOrderId = FindChildValue(resultEl, "MPayOrderId")
            ?? throw new InvalidOperationException("MPayOrderId missing.");
        var redirectUrl = FindChildValue(resultEl, "RedirectUrl")
            ?? throw new InvalidOperationException("RedirectUrl missing.");
        return new MPayPostOrderResult(mpayOrderId, new Uri(redirectUrl, UriKind.Absolute));
    }

    /// <summary>
    /// Parses the <c>&lt;GetOrderStatusResponse&gt;</c> element returned by MPay.
    /// </summary>
    /// <param name="body">The <c>&lt;soap:Body&gt;</c> XElement.</param>
    private static MPayOrderStatus ParseGetOrderStatusResponse(XElement body)
    {
        var resultEl = body
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "GetOrderStatusResult")
            ?? throw new InvalidOperationException("GetOrderStatusResult element missing.");

        var orderId = FindChildValue(resultEl, "OrderId")
            ?? throw new InvalidOperationException("OrderId missing.");
        var stateRaw = FindChildValue(resultEl, "State")
            ?? throw new InvalidOperationException("State missing.");
        var amountRaw = FindChildValue(resultEl, "AmountMdl")
            ?? throw new InvalidOperationException("AmountMdl missing.");
        var paymentRef = FindChildValue(resultEl, "PaymentRef");
        var confirmedRaw = FindChildValue(resultEl, "ConfirmedAtUtc");

        if (!Enum.TryParse<MPayOrderState>(stateRaw, ignoreCase: true, out var state))
        {
            throw new InvalidOperationException($"Unknown MPay state '{stateRaw}'.");
        }
        var amount = decimal.Parse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture);
        DateTime? confirmedAt = null;
        if (!string.IsNullOrEmpty(confirmedRaw))
        {
            confirmedAt = DateTime.Parse(
                confirmedRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        return new MPayOrderStatus(orderId, state, amount, paymentRef, confirmedAt);
    }

    /// <summary>
    /// Locates a direct or descendant child element by local name (ignores namespace).
    /// Returns its text content, or <c>null</c> when the element is absent.
    /// </summary>
    /// <param name="parent">The element to search under.</param>
    /// <param name="localName">The local element name to match — namespace-insensitive.</param>
    private static string? FindChildValue(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Best-effort extractor for the SOAP fault string inside an error response body.
    /// Returns <c>null</c> when the body is not parseable or doesn't contain a fault.
    /// </summary>
    /// <param name="responseText">The raw upstream response body.</param>
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
