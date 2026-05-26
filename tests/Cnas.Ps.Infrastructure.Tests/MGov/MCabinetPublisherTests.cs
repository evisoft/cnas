using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MCabinetPublisher"/>. Exercises the outbound publish + retire
/// paths, the base-URL safety guard, the bearer-auth header, and the upstream-failure
/// error code mapping. Drives the HTTP boundary with a stub <see cref="CapturingHandler"/>
/// (mirrors <see cref="MDocsClientTests"/>).
/// </summary>
public class MCabinetPublisherTests
{
    private const string BaseUrl = "https://mcabinet.example.gov.md";
    private const string SystemCode = "CNAS-PS";

    /// <summary>Builds an <see cref="MCabinetPublisher"/> wired to a canned <see cref="CapturingHandler"/>.</summary>
    /// <param name="respond">Synchronous responder returning the canned upstream reply.</param>
    /// <param name="baseUrl">Override the configured base URL (null/empty to disable the integration).</param>
    private static (MCabinetPublisher publisher, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
#pragma warning disable CS0618 // Bearer is [Obsolete] — set deliberately so the test can still assert legacy Bearer-header behaviour.
        var opts = Options.Create(new MCabinetOptions
        {
            BaseUrl = baseUrl ?? string.Empty,
            Bearer = "tk-xyz",
            SystemCode = SystemCode,
        });
#pragma warning restore CS0618
        var publisher = new MCabinetPublisher(http, opts, NullLogger<MCabinetPublisher>.Instance);
        return (publisher, handler);
    }

    /// <summary>Sample card representing a dossier accepted for examination.</summary>
    private static MCabinetCard SampleCard() => new(
        ExternalId: "k3Gq9",
        CitizenIdnp: "2000000000000",
        ServiceCode: "UC03.OldAgePension",
        Status: MCabinetStatus.InExamination,
        TitleRo: "Pensie pentru limită de vârstă",
        SubtitleRo: "D-2026-ABCD",
        EventUtc: new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc),
        DeepLink: new Uri("https://ps.cnas.md/dossier/k3Gq9"));

    [Fact]
    public async Task PublishCardAsync_HappyPath_Returns200AndPostsJsonBody()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await sut.PublishCardAsync(SampleCard());

        result.IsSuccess.Should().BeTrue();

        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/cards");

        // Body should round-trip through JSON with all card fields preserved verbatim.
        using var doc = JsonDocument.Parse(handler.LastBody);
        var root = doc.RootElement;
        root.GetProperty("systemCode").GetString().Should().Be(SystemCode);
        root.GetProperty("externalId").GetString().Should().Be("k3Gq9");
        root.GetProperty("citizenIdnp").GetString().Should().Be("2000000000000");
        root.GetProperty("serviceCode").GetString().Should().Be("UC03.OldAgePension");
        root.GetProperty("status").GetString().Should().Be("InExamination");
        root.GetProperty("titleRo").GetString().Should().Be("Pensie pentru limită de vârstă");
        root.GetProperty("subtitleRo").GetString().Should().Be("D-2026-ABCD");
        root.GetProperty("eventUtc").GetString().Should().Be("2026-05-19T10:00:00Z");
        root.GetProperty("deepLink").GetString().Should().Be("https://ps.cnas.md/dossier/k3Gq9");
    }

    [Fact]
    public async Task PublishCardAsync_BaseUrlEmpty_ReturnsMCabinetPublishFailed()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.PublishCardAsync(SampleCard());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MCabinetPublishFailed);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishCardAsync_Upstream500_ReturnsMCabinetPublishFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.PublishCardAsync(SampleCard());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MCabinetPublishFailed);
    }

    [Fact]
    public async Task PublishCardAsync_SetsBearerHeader()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await sut.PublishCardAsync(SampleCard());

        result.IsSuccess.Should().BeTrue();
        handler.Last.Headers.Authorization.Should().NotBeNull();
        handler.Last.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Last.Headers.Authorization!.Parameter.Should().Be("tk-xyz");
    }

    [Fact]
    public async Task RetireCardAsync_HappyPath_SendsDeleteToCorrectRoute()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.RetireCardAsync("k3Gq9");

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Delete);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/cards/{SystemCode}/k3Gq9");
    }

    [Fact]
    public async Task RetireCardAsync_Upstream404_ReturnsMCabinetPublishFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.RetireCardAsync("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MCabinetPublishFailed);
    }
}
