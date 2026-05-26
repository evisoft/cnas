using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for the SOAP/WS-Security <see cref="MPayClient"/>. Verifies wire shape
/// (SOAP envelope, SOAPAction header, content-type), parsing of <c>PostOrder</c> /
/// <c>GetOrderStatus</c> / <c>CancelOrder</c> responses, error mapping (transport,
/// upstream non-2xx, SOAP fault), the WS-Security header placeholder, and the legacy
/// <see cref="IMPayClient.SendAsync(MPayOutbound, System.Threading.CancellationToken)"/>
/// back-compat shim that internally drives <c>PostOrder</c>.
/// </summary>
public class MPayClientTests
{
    /// <summary>Default base URL used by every test that does not exercise the missing-config path.</summary>
    private const string BaseUrl = "https://mpay.example.gov.md:8443";

    /// <summary>
    /// Builds an <see cref="MPayClient"/> wired to a <see cref="CapturingHandler"/> so the
    /// test can inspect the outbound HTTP request shape. <see cref="MGovOptions.MPayBearer"/>
    /// is deliberately populated so the test asserts that the new SOAP path ignores it (mTLS
    /// replaces the Bearer header).
    /// </summary>
    /// <param name="respond">Synchronous responder driving the canned upstream response.</param>
    /// <param name="baseUrl">Override base URL (empty string disables outbound calls).</param>
    private static (MPayClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
#pragma warning disable CS0618 // MPayBearer is [Obsolete] — set deliberately to assert it is ignored.
        var opts = Options.Create(new MGovOptions
        {
            MPayBaseUrl = baseUrl ?? string.Empty,
            MPayBearer = "irrelevant-token",
        });
#pragma warning restore CS0618
        var orderStore = Substitute.For<IMPayOrderStore>();
        orderStore.CreateAsync(Arg.Any<MPayOrderSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var client = new MPayClient(http, opts, NullLogger<MPayClient>.Instance, new TestClock(), orderStore);
        return (client, handler);
    }

    /// <summary>Helper — builds a SOAP envelope containing the supplied body XML.</summary>
    private static string SoapEnvelope(string innerXml) =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        + "<s:Body>" + innerXml + "</s:Body>"
        + "</s:Envelope>";

    /// <summary>Helper — builds a SOAP-fault envelope for failure-path assertions.</summary>
    private static string SoapFault(string faultString) =>
        SoapEnvelope(
            "<s:Fault xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
            + "<faultcode>s:Server</faultcode>"
            + "<faultstring>" + faultString + "</faultstring>"
            + "</s:Fault>");

    /// <summary>Helper — standard <see cref="MPayPostOrderRequest"/> used by the happy-path tests.</summary>
    private static MPayPostOrderRequest SampleRequest(string orderId = "ORD-1") =>
        new(
            OrderId: orderId,
            AmountMdl: 1234.56m,
            CitizenIdnp: "2000000000007",
            ServiceCode: "CNAS.PENSION.AGE",
            DescriptionRo: "Plata contribuții CNAS",
            ReturnUrl: new Uri("https://cnas.example/return"),
            CorrelationId: null);

    /// <summary>Helper — successful <c>PostOrder</c> response envelope.</summary>
    private static string PostOrderResponseXml(string mpayOrderId, string redirectUrl) =>
        SoapEnvelope(
            "<PostOrderResponse xmlns=\"http://egov.md/MPay\">"
            + "<PostOrderResult>"
            + "<MPayOrderId>" + mpayOrderId + "</MPayOrderId>"
            + "<RedirectUrl>" + redirectUrl + "</RedirectUrl>"
            + "</PostOrderResult>"
            + "</PostOrderResponse>");

    [Fact]
    public async Task PostOrderAsync_HappyPath_PostsSoapEnvelopeWithSOAPActionHeader()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                PostOrderResponseXml("MPAY-1", "https://mpay.gov.md/service/pay?orderId=MPAY-1"),
                Encoding.UTF8,
                "text/xml"),
        });

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        handler.Captured.Should().HaveCount(1);

        var sent = handler.Last;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/MPay.svc");
        sent.Headers.GetValues("SOAPAction").Should().ContainSingle()
            .Which.Should().Be("\"http://egov.md/MPay/PostOrder\"");
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("text/xml");
        // mTLS replaces Bearer — Authorization header MUST be absent.
        sent.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task PostOrderAsync_BodyContainsAmountAndOrderIdAndReturnUrl()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                PostOrderResponseXml("MPAY-2", "https://mpay.gov.md/service/pay?orderId=MPAY-2"),
                Encoding.UTF8,
                "text/xml"),
        });

        var result = await sut.PostOrderAsync(SampleRequest("ORD-ABCD"));

        result.IsSuccess.Should().BeTrue();
        var body = handler.LastBody;
        body.Should().Contain("<PostOrder");
        body.Should().Contain("<OrderId>ORD-ABCD</OrderId>");
        body.Should().Contain("<AmountMdl>1234.56</AmountMdl>");
        body.Should().Contain("<CitizenIdnp>2000000000007</CitizenIdnp>");
        body.Should().Contain("<ServiceCode>CNAS.PENSION.AGE</ServiceCode>");
        body.Should().Contain("Plata contribuții CNAS");
        body.Should().Contain("<ReturnUrl>https://cnas.example/return</ReturnUrl>");
    }

    [Fact]
    public async Task PostOrderAsync_HappyPath_ReturnsMPayOrderIdAndRedirectUrl()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                PostOrderResponseXml("MPAY-77", "https://mpay.gov.md/service/pay?orderId=MPAY-77"),
                Encoding.UTF8,
                "text/xml"),
        });

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.MPayOrderId.Should().Be("MPAY-77");
        result.Value.RedirectUrl.AbsoluteUri.Should().Be("https://mpay.gov.md/service/pay?orderId=MPAY-77");
    }

    [Fact]
    public async Task PostOrderAsync_BaseUrlEmpty_ReturnsMPayFailedWithoutHttpCall()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MPayFailed);
        handler.Captured.Should().BeEmpty("no HTTP traffic should occur when MPayBaseUrl is empty");
    }

    [Fact]
    public async Task PostOrderAsync_Upstream500_ReturnsMPayFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MPayFailed);
    }

    [Fact]
    public async Task PostOrderAsync_SoapFault_ReturnsMPayFailedWithFaultString()
    {
        var faultXml = SoapFault("Order amount exceeds configured ceiling.");
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(faultXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MPayFailed);
        result.ErrorMessage.Should().Contain("Order amount exceeds configured ceiling.");
    }

    [Fact]
    public async Task GetOrderStatusAsync_HappyPath_ParsesStateAmountAndPaymentRef()
    {
        var responseXml = SoapEnvelope(
            "<GetOrderStatusResponse xmlns=\"http://egov.md/MPay\">"
            + "<GetOrderStatusResult>"
            + "<OrderId>ORD-1</OrderId>"
            + "<State>Confirmed</State>"
            + "<AmountMdl>1500.00</AmountMdl>"
            + "<PaymentRef>BANK-REF-42</PaymentRef>"
            + "<ConfirmedAtUtc>2026-05-20T10:30:00Z</ConfirmedAtUtc>"
            + "</GetOrderStatusResult>"
            + "</GetOrderStatusResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.GetOrderStatusAsync("ORD-1");

        result.IsSuccess.Should().BeTrue();
        result.Value.OrderId.Should().Be("ORD-1");
        result.Value.State.Should().Be(MPayOrderState.Confirmed);
        result.Value.AmountMdl.Should().Be(1500.00m);
        result.Value.PaymentRef.Should().Be("BANK-REF-42");
        result.Value.ConfirmedAtUtc.Should().Be(new DateTime(2026, 5, 20, 10, 30, 0, DateTimeKind.Utc));

        handler.Last.Headers.GetValues("SOAPAction").Single()
            .Should().Be("\"http://egov.md/MPay/GetOrderStatus\"");
        handler.LastBody.Should().Contain("<OrderId>ORD-1</OrderId>");
    }

    [Fact]
    public async Task CancelOrderAsync_HappyPath_PostsCancelEnvelopeWithReason()
    {
        var responseXml = SoapEnvelope(
            "<CancelOrderResponse xmlns=\"http://egov.md/MPay\">"
            + "<CancelOrderResult>"
            + "<Acknowledged>true</Acknowledged>"
            + "</CancelOrderResult>"
            + "</CancelOrderResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.CancelOrderAsync("ORD-9", "Citizen withdrew the application.");

        result.IsSuccess.Should().BeTrue();
        handler.Last.Headers.GetValues("SOAPAction").Single()
            .Should().Be("\"http://egov.md/MPay/CancelOrder\"");
        var body = handler.LastBody;
        body.Should().Contain("<CancelOrder");
        body.Should().Contain("<OrderId>ORD-9</OrderId>");
        body.Should().Contain("<Reason>Citizen withdrew the application.</Reason>");
    }

    [Fact]
    public async Task PostOrderAsync_EnvelopeIncludesWSSecurityHeaderPlaceholder()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                PostOrderResponseXml("MPAY-5", "https://mpay.gov.md/service/pay?orderId=MPAY-5"),
                Encoding.UTF8,
                "text/xml"),
        });

        var result = await sut.PostOrderAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        var body = handler.LastBody;
        // Structural-presence assertion: the empty WS-Security header element must be
        // present in the SOAP envelope so the production X.509 XML-DSig insertion has a
        // landing spot when the WSDL is obtained from suport.mpay@gov.md.
        body.Should().Contain("<wsse:Security",
            "the SOAP envelope must reserve the WS-Security header element for the future X.509 XML-DSig signature");
    }

    [Fact]
    public async Task SendAsync_LegacyShim_DrivesPostOrder_AndReturnsReceipt()
    {
        var responseXml = PostOrderResponseXml(
            "MPAY-LEGACY-1",
            "https://mpay.gov.md/service/pay?orderId=MPAY-LEGACY-1");
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var legacyPayload = new MPayOutbound(
            BeneficiaryIdnp: "2000000000000",
            BeneficiaryIban: "MD24AG000225100013104168",
            AmountMdl: 1234.56m,
            Reference: "REF-LEG-1");

        var result = await sut.SendAsync(legacyPayload);

        result.IsSuccess.Should().BeTrue();
        result.Value.TransactionId.Should().Be("MPAY-LEGACY-1");
        // The legacy receipt's Status echoes the new "Pending" state since the order was
        // just posted and not yet confirmed (the browser-redirect step is out of scope for
        // a server-to-server shim).
        result.Value.Status.Should().NotBeNullOrWhiteSpace();
        // Wire: a single PostOrder SOAP call to MPay.svc.
        handler.Captured.Should().HaveCount(1);
        handler.Last.Headers.GetValues("SOAPAction").Single()
            .Should().Be("\"http://egov.md/MPay/PostOrder\"");
    }
}
