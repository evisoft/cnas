using System.Net;
using System.Net.Http;
using Cnas.Ps.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Unit tests for <see cref="HttpReachabilityProbe"/> — the shared 2-second GET probe used
/// by every per-MGov-service health-check adapter. Verifies the mapping table documented
/// on the helper.
/// </summary>
public sealed class HttpReachabilityProbeTests
{
    [Fact]
    public async Task Probe_NullBaseUrl_ReturnsDegraded()
    {
        // Arrange — handler should never be called when base URL is empty.
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        // Act
        var result = await HttpReachabilityProbe.ProbeAsync(http, baseUrl: null, probeName: "X");

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_WhitespaceBaseUrl_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(http, baseUrl: "   ", probeName: "X");

        result.Status.Should().Be(HealthStatus.Degraded);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_Returns200_ReturnsHealthy()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(http, "https://msign.test", "MSign");

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.Captured.Should().HaveCount(1);
        handler.Captured[0].Method.Should().Be(HttpMethod.Get);
        handler.Captured[0].RequestUri!.AbsoluteUri.Should().Be("https://msign.test/health");
    }

    [Fact]
    public async Task Probe_Returns404_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(http, "https://msign.test", "MSign");

        // 404 means upstream is reachable but the probe path is missing — that's degraded,
        // not unhealthy. The service may simply not expose /health.
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Probe_Returns500_ReturnsUnhealthy()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(http, "https://msign.test", "MSign");

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Probe_NetworkFailure_ReturnsUnhealthy()
    {
        var handler = TestHttpHandler.AlwaysThrow(new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(http, "https://msign.test", "MSign");

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Probe_HonoursCustomRelativePath()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        var result = await HttpReachabilityProbe.ProbeAsync(
            http, "https://operaton.test", "Workflow",
            relativeProbePath: "/engine-rest/engine/");

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].RequestUri!.AbsoluteUri.Should().Be("https://operaton.test/engine-rest/engine/");
    }

    [Fact]
    public async Task Probe_MalformedBaseUrl_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        // "::not a url" is not a valid absolute URI.
        var result = await HttpReachabilityProbe.ProbeAsync(http, "::not a url::", "X");

        result.Status.Should().Be(HealthStatus.Degraded);
        handler.Captured.Should().BeEmpty();
    }
}
