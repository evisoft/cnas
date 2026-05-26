using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for the SOAP <see cref="MConnectClient"/>. Verifies wire shape (SOAP
/// envelope with <c>wsse:Security</c> placeholder header, quoted SOAPAction, text/xml
/// content-type, CDATA-wrapped JSON payload), success-path response extraction,
/// error mapping, and the no-Authorization-header invariant that confirms the client
/// has been moved off the legacy Bearer-token model onto mTLS.
/// </summary>
public class MConnectClientTests
{
    /// <summary>Stable base URL used in every positive-path test.</summary>
    private const string BaseUrl = "https://mconnect.example.gov.md";

    /// <summary>
    /// Builds a SUT wired to a <see cref="CapturingHandler"/> for assertion of the
    /// outbound HTTP shape and a deterministic clock so any time-derived header is
    /// reproducible across runs.
    /// </summary>
    /// <param name="respond">Function producing the canned upstream response.</param>
    /// <param name="baseUrl">Override base URL; <c>null</c> uses <see cref="BaseUrl"/>; empty disables the client.</param>
    private static (MConnectClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new MGovOptions
        {
            MConnectBaseUrl = baseUrl ?? string.Empty,
#pragma warning disable CS0618 // MConnectBearer is [Obsolete] — set deliberately to assert it is ignored.
            MConnectBearer = "irrelevant-token",
#pragma warning restore CS0618
        });
        var client = new MConnectClient(http, opts, NullLogger<MConnectClient>.Instance, new TestClock());
        return (client, handler);
    }

    /// <summary>Helper — wraps a body fragment in a SOAP 1.2 envelope (matches the response-side parse).</summary>
    private static string SoapEnvelope(string innerXml) =>
        "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
        + "<soap:Body>" + innerXml + "</soap:Body>"
        + "</soap:Envelope>";

    /// <summary>Helper — builds a SOAP-fault envelope for failure-path assertions.</summary>
    private static string SoapFault(string faultString) =>
        SoapEnvelope(
            "<soap:Fault xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
            + "<faultcode>soap:Server</faultcode>"
            + "<faultstring>" + faultString + "</faultstring>"
            + "</soap:Fault>");

    /// <summary>Helper — builds a happy-path CallResponse envelope carrying the supplied JSON.</summary>
    private static string CallResponseEnvelope(string responseJson) =>
        SoapEnvelope(
            "<CallResponse xmlns=\"http://egov.md/MConnect\">"
            + "<ResponseJson>" + System.Security.SecurityElement.Escape(responseJson) + "</ResponseJson>"
            + "</CallResponse>");

    [Fact]
    public async Task CallAsync_HappyPath_PostsSoapEnvelopeToMConnectSvc()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{\"ok\":true}"), Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{\"idnp\":\"2000000000000\"}");

        result.IsSuccess.Should().BeTrue();
        handler.Captured.Should().HaveCount(1);
        var sent = handler.Last;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/MConnect.svc");
    }

    [Fact]
    public async Task CallAsync_HappyPath_SOAPActionHeaderIsQuoted()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{}"), Encoding.UTF8, "text/xml"),
        });

        await sut.CallAsync("RSP.GetPerson", "{}");

        handler.Last.Headers.GetValues("SOAPAction").Should().ContainSingle()
            .Which.Should().Be("\"http://egov.md/MConnect/Call\"",
                "SOAPAction must be quoted per WS-I Basic Profile 1.1 §R2744");
    }

    [Fact]
    public async Task CallAsync_ContentTypeIsTextXml()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{}"), Encoding.UTF8, "text/xml"),
        });

        await sut.CallAsync("RSP.GetPerson", "{}");

        handler.Last.Content!.Headers.ContentType!.MediaType.Should().Be("text/xml");
    }

    [Fact]
    public async Task CallAsync_BodyContainsServiceCodeAndRequestJson()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{}"), Encoding.UTF8, "text/xml"),
        });

        const string requestJson = "{\"idnp\":\"2000000000000\",\"asOfUtc\":\"2026-05-20T08:00:00Z\"}";
        await sut.CallAsync("RSP.GetPerson", requestJson);

        var body = handler.LastBody;
        body.Should().Contain("<ServiceCode>RSP.GetPerson</ServiceCode>");
        body.Should().Contain("<RequestJson><![CDATA[" + requestJson + "]]></RequestJson>",
            "request JSON must be CDATA-wrapped so embedded < and & characters do not corrupt the envelope");
    }

    [Fact]
    public async Task CallAsync_EnvelopeIncludesWSSecurityHeaderPlaceholder()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{}"), Encoding.UTF8, "text/xml"),
        });

        await sut.CallAsync("RSP.GetPerson", "{}");

        var body = handler.LastBody;
        body.Should().Contain("wsse:Security",
            "the envelope must reserve a WS-Security header so the production X.509 XML-DSig signature can be wired in once the per-system MConnect contract is obtained from MEGA");
        body.Should().Contain("TODO[mconnect-wss]",
            "the placeholder must carry the canonical TODO marker so a grep across MGov clients surfaces every unsigned envelope");
    }

    [Fact]
    public async Task CallAsync_HappyPath_ReturnsResponseJsonFromCallResponse()
    {
        const string upstreamJson = "{\"name\":\"Ion\",\"surname\":\"Popescu\"}";
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope(upstreamJson), Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(upstreamJson);
    }

    [Fact]
    public async Task CallAsync_BaseUrlEmpty_ReturnsMConnectFailedWithoutHttpCall()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
        result.ErrorMessage.Should().Contain("BaseUrl not configured");
        handler.Captured.Should().BeEmpty("the client must short-circuit before any HTTP traffic when the base URL is not configured");
    }

    [Fact]
    public async Task CallAsync_Upstream500_ReturnsMConnectFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream-down"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task CallAsync_SoapFault_ReturnsMConnectFailedWithFaultString()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SoapFault("Service unavailable for code RSP.GetPerson"), Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
        result.ErrorMessage.Should().Contain("Service unavailable for code RSP.GetPerson");
    }

    [Fact]
    public async Task CallAsync_AuthorizationHeaderNotSet()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{}"), Encoding.UTF8, "text/xml"),
        });

        await sut.CallAsync("RSP.GetPerson", "{}");

        handler.Last.Headers.Authorization.Should().BeNull(
            "MConnect uses mTLS exclusively; no Authorization header should ever appear on outbound requests");
    }

    [Fact]
    public async Task CallAsync_UnparseableResponse_ReturnsMConnectFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-xml-at-all", Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task CallAsync_MissingCallResponseElement_ReturnsMConnectFailed()
    {
        // SOAP envelope is structurally valid but missing the expected CallResponse body.
        var bogus = SoapEnvelope("<UnexpectedResponse xmlns=\"http://egov.md/MConnect\"/>");
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bogus, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CallAsync("RSP.GetPerson", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
