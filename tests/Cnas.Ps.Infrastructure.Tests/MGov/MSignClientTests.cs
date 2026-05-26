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
/// Unit tests for the two-phase SOAP <see cref="MSignClient"/>. Verifies wire shape
/// (SOAP envelope, SOAPAction header, content-type), error mapping, and the legacy
/// <see cref="IMSignClient.SignAsync(MSignRequest, System.Threading.CancellationToken)"/>
/// back-compat shim that chains PostSignRequest → IsRequestReady → GetSignResponse.
/// </summary>
public class MSignClientTests
{
    private const string BaseUrl = "https://msign.example.gov.md";

    /// <summary>
    /// Builds a SUT wired to a <see cref="CapturingHandler"/> for assertion of the
    /// outbound HTTP shape. The poll interval / iteration ceiling are kept at their
    /// production defaults — tests that exercise the polling path override via the
    /// public constants on <see cref="MSignClient"/>.
    /// </summary>
    private static (MSignClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
#pragma warning disable CS0618 // MSignBearer is [Obsolete] — set deliberately to assert it is ignored.
        var opts = Options.Create(new MGovOptions
        {
            MSignBaseUrl = baseUrl ?? string.Empty,
            MSignBearer = "irrelevant-token",
        });
#pragma warning restore CS0618
        var client = new MSignClient(http, opts, NullLogger<MSignClient>.Instance, new TestClock());
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

    [Fact]
    public async Task PostSignRequestAsync_HappyPath_PostsSoapEnvelopeWithSOAPActionHeader()
    {
        var responseXml = SoapEnvelope(
            "<PostSignRequestResponse xmlns=\"http://egov.md/MSign\">"
            + "<PostSignRequestResult>"
            + "<RequestId>R-1</RequestId>"
            + "<RedirectUrl>https://msign.gov.md/R-1?ReturnUrl=https%3A%2F%2Fcnas.example%2Freturn</RedirectUrl>"
            + "</PostSignRequestResult>"
            + "</PostSignRequestResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var req = new MSignPostSignRequest(
            DocumentBytes: new byte[] { 1, 2, 3 },
            DocumentName: "decizia.pdf",
            ContentType: "application/pdf",
            Mode: MSignContentMode.PdfBytes,
            ReturnUrl: new Uri("https://cnas.example/return"),
            CorrelationId: null);

        var result = await sut.PostSignRequestAsync(req);

        result.IsSuccess.Should().BeTrue();
        handler.Captured.Should().HaveCount(1);

        var sent = handler.Last;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/MSign.svc");
        sent.Headers.GetValues("SOAPAction").Should().ContainSingle()
            .Which.Should().Be("\"http://egov.md/MSign/PostSignRequest\"");
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("text/xml");

        var body = handler.LastBody;
        body.Should().Contain("<PostSignRequest");
        body.Should().Contain("<DocumentName>decizia.pdf</DocumentName>");
        body.Should().Contain("<ContentType>application/pdf</ContentType>");
        body.Should().Contain("<ContentMode>PdfBytes</ContentMode>");
        body.Should().Contain("<ReturnUrl>https://cnas.example/return</ReturnUrl>");
        // Bearer must NOT be present — mTLS replaces the Authorization header.
        sent.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task PostSignRequestAsync_HappyPath_ReturnsRequestIdAndRedirectUrl()
    {
        var responseXml = SoapEnvelope(
            "<PostSignRequestResponse xmlns=\"http://egov.md/MSign\">"
            + "<PostSignRequestResult>"
            + "<RequestId>R-42</RequestId>"
            + "<RedirectUrl>https://msign.gov.md/R-42?ReturnUrl=https%3A%2F%2Fcnas%2Freturn</RedirectUrl>"
            + "</PostSignRequestResult>"
            + "</PostSignRequestResponse>");

        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: new byte[] { 9 },
            DocumentName: "x.pdf",
            ContentType: "application/pdf",
            Mode: MSignContentMode.Hash,
            ReturnUrl: new Uri("https://cnas/return"),
            CorrelationId: null));

        result.IsSuccess.Should().BeTrue();
        result.Value.RequestId.Should().Be("R-42");
        result.Value.RedirectUrl.AbsoluteUri.Should().Be("https://msign.gov.md/R-42?ReturnUrl=https%3A%2F%2Fcnas%2Freturn");
    }

    [Fact]
    public async Task PostSignRequestAsync_BaseUrlEmpty_ReturnsMSignFailedWithoutHttpCall()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: new byte[] { 1 },
            DocumentName: "x.pdf",
            ContentType: "application/pdf",
            Mode: MSignContentMode.PdfBytes,
            ReturnUrl: new Uri("https://cnas/return"),
            CorrelationId: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MSignFailed);
        result.ErrorMessage.Should().Contain("BaseUrl");
        handler.Captured.Should().BeEmpty("no HTTP traffic should occur when base URL is empty");
    }

    [Fact]
    public async Task PostSignRequestAsync_Upstream500_ReturnsMSignFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: new byte[] { 1 },
            DocumentName: "x.pdf",
            ContentType: "application/pdf",
            Mode: MSignContentMode.PdfBytes,
            ReturnUrl: new Uri("https://cnas/return"),
            CorrelationId: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MSignFailed);
    }

    [Fact]
    public async Task PostSignRequestAsync_SoapFault_ReturnsMSignFailedWithFaultString()
    {
        var faultXml = SoapFault("Document content rejected by validator.");
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(faultXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: new byte[] { 1 },
            DocumentName: "x.pdf",
            ContentType: "application/pdf",
            Mode: MSignContentMode.PdfBytes,
            ReturnUrl: new Uri("https://cnas/return"),
            CorrelationId: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MSignFailed);
        result.ErrorMessage.Should().Contain("Document content rejected by validator.");
    }

    [Fact]
    public async Task GetSignResponseAsync_HappyPath_ReturnsBytesAndMetadata()
    {
        var signatureBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var responseXml = SoapEnvelope(
            "<GetSignResponseResponse xmlns=\"http://egov.md/MSign\">"
            + "<GetSignResponseResult>"
            + "<SignatureBase64>" + Convert.ToBase64String(signatureBytes) + "</SignatureBase64>"
            + "<SignedAtUtc>2026-05-19T10:30:00Z</SignedAtUtc>"
            + "<SignerIdnp>2000000000001</SignerIdnp>"
            + "<SignerFullName>Ion Popescu</SignerFullName>"
            + "<CertificateThumbprint>ABCDEF0123456789</CertificateThumbprint>"
            + "</GetSignResponseResult>"
            + "</GetSignResponseResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.GetSignResponseAsync("R-42");

        result.IsSuccess.Should().BeTrue();
        result.Value.SignatureBytes.Should().Equal(signatureBytes);
        result.Value.Metadata.SignerIdnp.Should().Be("2000000000001");
        result.Value.Metadata.SignerFullName.Should().Be("Ion Popescu");
        result.Value.Metadata.CertificateThumbprint.Should().Be("ABCDEF0123456789");
        result.Value.Metadata.SignedAtUtc.Should().Be(new DateTime(2026, 5, 19, 10, 30, 0, DateTimeKind.Utc));

        var sent = handler.Last;
        sent.Headers.GetValues("SOAPAction").Should().ContainSingle()
            .Which.Should().Be("\"http://egov.md/MSign/GetSignResponse\"");
        handler.LastBody.Should().Contain("<RequestId>R-42</RequestId>");
    }

    [Fact]
    public async Task IsRequestReadyAsync_True_ReturnsTrue()
    {
        var responseXml = SoapEnvelope(
            "<IsRequestReadyResponse xmlns=\"http://egov.md/MSign\">"
            + "<IsRequestReadyResult>"
            + "<Ready>true</Ready>"
            + "</IsRequestReadyResult>"
            + "</IsRequestReadyResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.IsRequestReadyAsync("R-42");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        handler.Last.Headers.GetValues("SOAPAction").Should().ContainSingle()
            .Which.Should().Be("\"http://egov.md/MSign/IsRequestReady\"");
    }

    [Fact]
    public async Task IsRequestReadyAsync_False_ReturnsFalse()
    {
        var responseXml = SoapEnvelope(
            "<IsRequestReadyResponse xmlns=\"http://egov.md/MSign\">"
            + "<IsRequestReadyResult>"
            + "<Ready>false</Ready>"
            + "</IsRequestReadyResult>"
            + "</IsRequestReadyResponse>");

        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var result = await sut.IsRequestReadyAsync("R-42");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task SignAsync_LegacyShim_DrivesTwoPhaseFlow_AndReturnsBytes()
    {
        // Three SOAP calls in sequence: PostSignRequest -> IsRequestReady(true) -> GetSignResponse.
        var step = 0;
        var (sut, handler) = Build(req =>
        {
            step++;
            var action = req.Headers.GetValues("SOAPAction").Single();
            if (action.Contains("PostSignRequest"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoapEnvelope(
                        "<PostSignRequestResponse xmlns=\"http://egov.md/MSign\">"
                        + "<PostSignRequestResult>"
                        + "<RequestId>RID-100</RequestId>"
                        + "<RedirectUrl>https://msign.gov.md/RID-100</RedirectUrl>"
                        + "</PostSignRequestResult>"
                        + "</PostSignRequestResponse>"), Encoding.UTF8, "text/xml"),
                };
            }
            if (action.Contains("IsRequestReady"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoapEnvelope(
                        "<IsRequestReadyResponse xmlns=\"http://egov.md/MSign\">"
                        + "<IsRequestReadyResult><Ready>true</Ready></IsRequestReadyResult>"
                        + "</IsRequestReadyResponse>"), Encoding.UTF8, "text/xml"),
                };
            }
            // GetSignResponse
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoapEnvelope(
                    "<GetSignResponseResponse xmlns=\"http://egov.md/MSign\">"
                    + "<GetSignResponseResult>"
                    + "<SignatureBase64>" + Convert.ToBase64String(new byte[] { 0xAB, 0xCD }) + "</SignatureBase64>"
                    + "<SignedAtUtc>2026-05-19T10:30:00Z</SignedAtUtc>"
                    + "<SignerIdnp>2000000000001</SignerIdnp>"
                    + "<SignerFullName>Ion Popescu</SignerFullName>"
                    + "<CertificateThumbprint>ABC</CertificateThumbprint>"
                    + "</GetSignResponseResult>"
                    + "</GetSignResponseResponse>"), Encoding.UTF8, "text/xml"),
            };
        });

        // Set a very small poll interval so the test is fast.
        sut.LegacyPollInterval = TimeSpan.FromMilliseconds(1);
        sut.LegacyMaxPollIterations = 5;

        var result = await sut.SignAsync(new MSignRequest(new byte[] { 1, 2, 3 }, "PNOMD-0000000000000", "Decision DA-12"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Signature.Should().Equal(new byte[] { 0xAB, 0xCD });
        result.Value.ProtocolReference.Should().Be("RID-100");
        handler.Captured.Should().HaveCount(3, "shim makes exactly PostSignRequest + IsRequestReady + GetSignResponse");
    }

    [Fact]
    public async Task SignAsync_LegacyShim_PollTimeout_ReturnsMSignFailed()
    {
        // IsRequestReady always returns false; legacy shim must give up after the
        // configured poll-iteration ceiling and surface MSIGN_FAILED.
        var (sut, _) = Build(req =>
        {
            var action = req.Headers.GetValues("SOAPAction").Single();
            if (action.Contains("PostSignRequest"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoapEnvelope(
                        "<PostSignRequestResponse xmlns=\"http://egov.md/MSign\">"
                        + "<PostSignRequestResult>"
                        + "<RequestId>RID-200</RequestId>"
                        + "<RedirectUrl>https://msign.gov.md/RID-200</RedirectUrl>"
                        + "</PostSignRequestResult>"
                        + "</PostSignRequestResponse>"), Encoding.UTF8, "text/xml"),
                };
            }
            // Always not-ready.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoapEnvelope(
                    "<IsRequestReadyResponse xmlns=\"http://egov.md/MSign\">"
                    + "<IsRequestReadyResult><Ready>false</Ready></IsRequestReadyResult>"
                    + "</IsRequestReadyResponse>"), Encoding.UTF8, "text/xml"),
            };
        });
        sut.LegacyPollInterval = TimeSpan.FromMilliseconds(1);
        sut.LegacyMaxPollIterations = 3;

        var result = await sut.SignAsync(new MSignRequest(new byte[] { 1 }, "subject", "r"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MSignFailed);
        result.ErrorMessage.Should().Contain("not ready");
    }

    [Fact]
    public async Task PostSignRequestAsync_HashMode_EmbedsBase64Hash()
    {
        var responseXml = SoapEnvelope(
            "<PostSignRequestResponse xmlns=\"http://egov.md/MSign\">"
            + "<PostSignRequestResult>"
            + "<RequestId>RH-1</RequestId>"
            + "<RedirectUrl>https://msign.gov.md/RH-1</RedirectUrl>"
            + "</PostSignRequestResult>"
            + "</PostSignRequestResponse>");

        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
        });

        var hash = new byte[] { 0x10, 0x20, 0x30 };
        var result = await sut.PostSignRequestAsync(new MSignPostSignRequest(
            DocumentBytes: hash,
            DocumentName: "hash.bin",
            ContentType: "application/octet-stream",
            Mode: MSignContentMode.Hash,
            ReturnUrl: new Uri("https://cnas/return"),
            CorrelationId: "trace-123"));

        result.IsSuccess.Should().BeTrue();
        var body = handler.LastBody;
        body.Should().Contain("<ContentMode>Hash</ContentMode>");
        body.Should().Contain("<ContentBase64>" + Convert.ToBase64String(hash) + "</ContentBase64>");
        handler.Last.Headers.GetValues("X-Correlation-Id").Single().Should().Be("trace-123");
    }
}
