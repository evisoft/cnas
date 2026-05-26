using System.Net;
using System.Net.Http;
using Cnas.Ps.Api.Health;
using Cnas.Ps.Infrastructure.Workflow;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Unit tests for <see cref="WorkflowEngineHealthCheck"/>. Verifies the unconfigured-degraded
/// and reachable-healthy cases, plus that the probe targets the Operaton identity endpoint.
/// </summary>
public sealed class WorkflowEngineHealthCheckTests
{
    private static IOptions<WorkflowOptions> Options_(Action<WorkflowOptions>? mutate = null)
    {
        var opts = new WorkflowOptions();
        mutate?.Invoke(opts);
        return Options.Create(opts);
    }

    [Fact]
    public async Task WorkflowEngineHealthCheck_BaseUrlUnconfigured_ReturnsDegraded()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new WorkflowEngineHealthCheck(new StubHttpClientFactory(new HttpClient(handler)), Options_());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowEngineHealthCheck_BaseUrlReachable_ReturnsHealthy_AndQueriesEngineRest()
    {
        var handler = TestHttpHandler.AlwaysStatus(HttpStatusCode.OK);
        var sut = new WorkflowEngineHealthCheck(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options_(o => o.OperatonBaseUrl = "https://operaton.test"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.Captured.Should().ContainSingle();
        // The Operaton identity endpoint is /engine-rest/engine/ — the probe MUST hit that path.
        handler.Captured[0].RequestUri!.AbsolutePath.Should().Be("/engine-rest/engine/");
    }
}
