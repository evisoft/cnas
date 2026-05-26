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
/// Unit tests for <see cref="MDocsClient"/>. Verifies the upload / download / metadata
/// paths, the empty-base-url safety guard, and the upstream-failure error code mapping.
/// </summary>
public class MDocsClientTests
{
    private const string BaseUrl = "https://mdocs.example.gov.md";

    /// <summary>Builds an <see cref="MDocsClient"/> wired to a canned <see cref="CapturingHandler"/>.</summary>
    private static (MDocsClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
#pragma warning disable CS0618 // MDocsBearer is [Obsolete] — set deliberately so the test can still assert legacy Bearer-header behaviour.
        var opts = Options.Create(new MGovOptions
        {
            MDocsBaseUrl = baseUrl ?? string.Empty,
            MDocsBearer = "tk",
        });
#pragma warning restore CS0618
        var client = new MDocsClient(http, opts, NullLogger<MDocsClient>.Instance, new TestClock());
        return (client, handler);
    }

    private static MDocsUploadRequest SampleRequest() => new(
        FileName: "decizia.pdf",
        ContentType: "application/pdf",
        Content: Encoding.ASCII.GetBytes("%PDF-1.4 hello"),
        CategoryCode: "CNAS.DECISION",
        Tags: new Dictionary<string, string> { ["dossierId"] = "DOSS-SQID" });

    [Fact]
    public async Task UploadAsync_NullRequest_Throws()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Func<Task> act = () => sut.UploadAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UploadAsync_BaseUrlUnconfigured_ReturnsInternal()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.UploadAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Internal);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadAsync_HappyPath_ReturnsReceipt()
    {
        // language=json
        const string responseBody =
            """{"documentId":"DOC-42","version":"v1","sha256":"abc123","uploadedAtUtc":"2026-05-19T10:00:00Z"}""";
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var result = await sut.UploadAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentId.Should().Be("DOC-42");
        result.Value.Version.Should().Be("v1");
        result.Value.Sha256.Should().Be("abc123");
        result.Value.UploadedAtUtc.Should().Be(new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc));

        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/v1/documents");
        handler.Last.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Last.Headers.Authorization!.Parameter.Should().Be("tk");
        handler.Last.Headers.GetValues("X-Correlation-Id").Should().ContainSingle();
    }

    [Fact]
    public async Task UploadAsync_UpstreamReturns500_ReturnsMDocsFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.UploadAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MDocsFailed);
    }

    [Fact]
    public async Task GetMetadataAsync_HappyPath_ParsesResponse()
    {
        // language=json
        const string responseBody =
            """
            {
              "documentId":"DOC-42",
              "fileName":"decizia.pdf",
              "contentType":"application/pdf",
              "sizeBytes":1234,
              "sha256":"abc123",
              "version":"v2",
              "uploadedAtUtc":"2026-05-19T10:00:00Z",
              "tags":{"dossierId":"DOSS-SQID"}
            }
            """;
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var result = await sut.GetMetadataAsync("DOC-42");

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentId.Should().Be("DOC-42");
        result.Value.FileName.Should().Be("decizia.pdf");
        result.Value.ContentType.Should().Be("application/pdf");
        result.Value.SizeBytes.Should().Be(1234);
        result.Value.Sha256.Should().Be("abc123");
        result.Value.Version.Should().Be("v2");
        result.Value.UploadedAtUtc.Should().Be(new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc));
        result.Value.Tags.Should().ContainKey("dossierId").WhoseValue.Should().Be("DOSS-SQID");

        handler.Last.Method.Should().Be(HttpMethod.Get);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/v1/documents/DOC-42");
    }
}
