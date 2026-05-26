using System.Net;
using System.Net.Http;
using Cnas.Ps.Api.Health;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Unit tests for the MGov-platform <see cref="IHealthCheck"/> adapters
/// (<see cref="MSignHealthCheck"/>, <see cref="MPayHealthCheck"/>, etc.). Each test
/// builds the adapter directly with a substituted <see cref="IHttpClientFactory"/> so
/// no real network call is made.
/// </summary>
public sealed class MGovHealthCheckTests
{
    /// <summary>Builds an <see cref="MGovOptions"/> with every base URL empty.</summary>
    private static IOptions<MGovOptions> UnconfiguredOptions() => Options.Create(new MGovOptions());

    /// <summary>Builds an <see cref="MGovOptions"/> populated by the supplied mutator.</summary>
    private static IOptions<MGovOptions> ConfiguredOptions(Action<MGovOptions> mutate)
    {
        var opts = new MGovOptions();
        mutate(opts);
        return Options.Create(opts);
    }

    /// <summary>Builds a stub <see cref="IHttpClientFactory"/> backed by the given handler.</summary>
    private static StubHttpClientFactory Factory(TestHttpHandler handler) =>
        new(new HttpClient(handler));

    // ───── MSign ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MSignHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MSignHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task MSignHealthCheck_BaseUrlReachable_ReturnsHealthy()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MSignHealthCheck(
            Factory(handler),
            ConfiguredOptions(o => o.MSignBaseUrl = "https://msign.test"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.Captured.Should().ContainSingle();
    }

    // ───── MPay ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MPayHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MPayHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task MPayHealthCheck_Upstream5xx_ReturnsUnhealthy()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.BadGateway);
        var sut = new MPayHealthCheck(
            Factory(handler),
            ConfiguredOptions(o => o.MPayBaseUrl = "https://mpay.test"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    // ───── MConnect ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MConnectHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MConnectHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task MConnectHealthCheck_BaseUrlReachable_ReturnsHealthy()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MConnectHealthCheck(
            Factory(handler),
            ConfiguredOptions(o => o.MConnectBaseUrl = "https://mconnect.test"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // ───── MNotify ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MNotifyHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MNotifyHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    // ───── MLog ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MLogHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MLogHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task MLogHealthCheck_NetworkFailure_ReturnsUnhealthy()
    {
        var handler = TestHttpHandler.AlwaysThrow(new HttpRequestException("dns failure"));
        var sut = new MLogHealthCheck(
            Factory(handler),
            ConfiguredOptions(o => o.MLogBaseUrl = "https://mlog.test"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    // ───── MPower ──────────────────────────────────────────────────────────────────────
    // MPower health check removed — MPower is consumed via MPass SAML claims, not a
    // standalone HTTP service. See docs/EGOV-INTEGRATION-GAP.md §"MPower".

    // ───── MConnect Events ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MConnectEventsHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MConnectEventsHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    // ───── MDocs ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MDocsHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new MDocsHealthCheck(Factory(handler), UnconfiguredOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }
}
