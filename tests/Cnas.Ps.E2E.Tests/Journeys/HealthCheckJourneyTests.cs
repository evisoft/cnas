using System.Net;
using Microsoft.Playwright;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// End-to-end journeys covering the public health-check surface of the API
/// (<c>/health/live</c> and <c>/health/ready</c>). Validates the contract that
/// operators (Kubernetes probes, load balancers, dashboards) depend on without
/// asserting any particular degraded/healthy color — only the endpoint shape.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class HealthCheckJourneyTests
{
    private readonly PlaywrightFixture _playwright;
    private readonly ApiHostFixture _api;

    /// <summary>Injects the shared fixtures supplied by the <see cref="E2ECollection"/>.</summary>
    public HealthCheckJourneyTests(PlaywrightFixture playwright, ApiHostFixture api)
    {
        _playwright = playwright;
        _api = api;
    }

    /// <summary>
    /// The /health/live endpoint is a pure process-alive ping and must return 200
    /// regardless of the state of external dependencies. This is the endpoint that
    /// orchestrator liveness probes hit — flapping here triggers a pod restart, so
    /// we lock its colour to Healthy.
    /// </summary>
    [Fact]
    public async Task LivenessEndpoint_Returns200()
    {
        // Arrange — fresh API request context pointing at the in-process Kestrel host.
        await using var ctx = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _api.BaseAddress,
            IgnoreHTTPSErrors = true,
        });

        // Act — hit the liveness probe.
        var response = await ctx.APIRequest.GetAsync("/health/live");

        // Assert — 200 OK and body advertises the Healthy status from the response
        // writer in HealthCheckResponses.
        response.Status.Should().Be((int)HttpStatusCode.OK);
        var body = await response.TextAsync();
        body.Should().Contain("Healthy", "liveness must never report a degraded status");
    }

    /// <summary>
    /// The /health/ready endpoint runs every dependency probe (DB, MGov, storage,
    /// workflow). In E2E those probes are deliberately unconfigured, so the
    /// aggregated status is allowed to be either 200 (Healthy/Degraded) or 503
    /// (Unhealthy) — this test locks the endpoint shape, not its current colour.
    /// </summary>
    [Fact]
    public async Task ReadinessEndpoint_Returns200OrServiceUnavailable()
    {
        // Arrange
        await using var ctx = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _api.BaseAddress,
            IgnoreHTTPSErrors = true,
        });

        // Act
        var response = await ctx.APIRequest.GetAsync("/health/ready");

        // Assert — endpoint must respond, and respond with one of the two shapes
        // documented in HealthCheckResponses.WriteJsonAsync.
        response.Status.Should().BeOneOf((int)HttpStatusCode.OK, (int)HttpStatusCode.ServiceUnavailable);
        var body = await response.TextAsync();
        body.Should().NotBeNullOrWhiteSpace("readiness must always return a JSON body");
    }
}
