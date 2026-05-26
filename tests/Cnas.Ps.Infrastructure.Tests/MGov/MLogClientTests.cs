using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MLogClient"/>. Verifies the central journal append wire shape
/// and the base-URL gate that keeps local dev from leaking events when MLog isn't configured.
/// </summary>
public class MLogClientTests
{
    private const string BaseUrl = "https://mlog.example.gov.md";

    private static (MLogClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new MGovOptions
        {
            MLogBaseUrl = baseUrl ?? string.Empty,
            MLogBearer = "tk",
        });
        var client = new MLogClient(http, opts, NullLogger<MLogClient>.Instance, new TestClock());
        return (client, handler);
    }

    private static MLogEntry SampleEntry() =>
        new("DECISION_APPROVED", "user-1", "Decision", 42L, "{\"amount\":100}");

    [Fact]
    public async Task AppendAsync_HappyPath_ReturnsSuccess()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { logId = "LOG-1" }),
        });

        var result = await sut.AppendAsync(SampleEntry());

        result.IsSuccess.Should().BeTrue();
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/v1/journal/append");
    }

    [Fact]
    public async Task AppendAsync_Returns500_ReturnsMLogFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.AppendAsync(SampleEntry());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MLogFailed);
    }

    [Fact]
    public async Task AppendAsync_BaseUrlUnconfigured_ReturnsInternal()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.AppendAsync(SampleEntry());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Internal);
        handler.Captured.Should().BeEmpty();
    }
}
