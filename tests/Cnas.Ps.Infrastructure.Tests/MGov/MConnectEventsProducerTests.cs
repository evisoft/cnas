using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MConnectEventsProducer"/>. Verifies CloudEvents v1.0 wire
/// shape, content-type negotiation, header decoration, and the base-URL gating that
/// keeps local-dev environments from accidentally posting to staging/production MEGA.
/// </summary>
public class MConnectEventsProducerTests
{
    private const string BaseUrl = "https://mconnect-events.example.gov.md";

    /// <summary>Builds a producer wired to a <see cref="CapturingHandler"/> for inspection.</summary>
    private static (MConnectEventsProducer client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
#pragma warning disable CS0618 // MConnectEventsBearer is [Obsolete] — set deliberately so the test can still assert legacy Bearer-header behaviour.
        var opts = Options.Create(new MGovOptions
        {
            MConnectEventsBaseUrl = baseUrl ?? string.Empty,
            MConnectEventsBearer = "tk",
        });
#pragma warning restore CS0618
        var client = new MConnectEventsProducer(http, opts, NullLogger<MConnectEventsProducer>.Instance, new TestClock());
        return (client, handler);
    }

    /// <summary>Canonical sample envelope used across most tests.</summary>
    private static CloudEventEnvelope SampleEnvelope() =>
        new(
            Id: "11111111-1111-1111-1111-111111111111",
            Source: "cnas-ps",
            Type: "md.cnas.ps.decision.issued.v1",
            TimeUtc: new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc),
            PartitionKey: "DA-2026-1",
            DataContentType: "application/json",
            DataJson: "{\"decisionRef\":\"DA-2026-1\"}");

    [Fact]
    public async Task PublishAsync_NullEnvelope_Throws()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var act = async () => await sut.PublishAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishAsync_BaseUrlUnconfigured_ReturnsInternal_NoHttpTraffic()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.PublishAsync(SampleEnvelope());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Internal);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_HappyPath_PostsCloudEventsJson_ContentType()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        var result = await sut.PublishAsync(SampleEnvelope());

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/ce/produce/event");
        handler.Last.Content!.Headers.ContentType!.MediaType.Should().Be("application/cloudevents+json");
        handler.Last.Content.Headers.ContentType.CharSet.Should().Be("utf-8");

        var body = handler.LastBody;
        body.Should().Contain("\"specversion\":\"1.0\"");
        body.Should().Contain("\"id\":\"11111111-1111-1111-1111-111111111111\"");
        body.Should().Contain("\"source\":\"cnas-ps\"");
        body.Should().Contain("\"type\":\"md.cnas.ps.decision.issued.v1\"");
        body.Should().Contain("\"partitionkey\":\"DA-2026-1\"");
        body.Should().Contain("\"datacontenttype\":\"application/json\"");
        // The raw data payload must be inlined (not double-encoded as a string).
        body.Should().Contain("\"data\":{\"decisionRef\":\"DA-2026-1\"}");
    }

    [Fact]
    public async Task PublishAsync_UpstreamReturns500_ReturnsMConnectFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.PublishAsync(SampleEnvelope());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task PublishAsync_AlwaysSendsCorrelationId()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await sut.PublishAsync(SampleEnvelope());

        handler.Last.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        correlationId.Should().HaveLength(16);
        correlationId.Should().MatchRegex("^[0-9a-f]{16}$");

        handler.Last.Headers.TryGetValues("X-Request-Date", out var dateValues).Should().BeTrue();
        dateValues!.Single().Should().NotBeNullOrEmpty();

        handler.Last.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Last.Headers.Authorization.Parameter.Should().Be("tk");
    }

    [Fact]
    public async Task PublishBatchAsync_PostsArrayBody_WithBatchContentType()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        var envelopes = new[]
        {
            SampleEnvelope(),
            SampleEnvelope() with { Id = "22222222-2222-2222-2222-222222222222", DataJson = "{\"decisionRef\":\"DA-2026-2\"}" },
        };

        var result = await sut.PublishBatchAsync(envelopes);

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/ce/produce/events");
        handler.Last.Content!.Headers.ContentType!.MediaType.Should().Be("application/cloudevents-batch+json");
        handler.Last.Content.Headers.ContentType.CharSet.Should().Be("utf-8");

        var body = handler.LastBody;
        body.TrimStart().Should().StartWith("[");
        body.TrimEnd().Should().EndWith("]");
        body.Should().Contain("\"id\":\"11111111-1111-1111-1111-111111111111\"");
        body.Should().Contain("\"id\":\"22222222-2222-2222-2222-222222222222\"");
    }
}
