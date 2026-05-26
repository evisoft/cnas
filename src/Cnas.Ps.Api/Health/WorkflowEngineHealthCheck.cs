using System.Net.Http;
using Cnas.Ps.Infrastructure.Workflow;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Readiness probe for the Operaton (Camunda 7-compatible) BPMN workflow engine.
/// Probes <c>{base}/engine-rest/engine/</c> — Camunda's stable identity endpoint that
/// returns a JSON array of registered engine names on a healthy install.
/// </summary>
/// <param name="httpClientFactory">Factory used to obtain a short-lived probe client.</param>
/// <param name="options">Bound workflow configuration snapshot.</param>
public sealed class WorkflowEngineHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<WorkflowOptions> options) : IHealthCheck
{
    /// <summary>Probe path tag — used for structured logging and tag-based filtering.</summary>
    private const string ProbePathTag = "workflow.operaton.ready";

    /// <summary>
    /// Relative endpoint queried on the engine. <c>/engine-rest/engine/</c> is the canonical
    /// "list registered engines" endpoint shipped by every Camunda 7 / Operaton REST
    /// distribution and requires no authentication on the default deployment.
    /// </summary>
    private const string ProbePath = "/engine-rest/engine/";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly WorkflowOptions _options = options.Value;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(ProbePathTag);
        return HttpReachabilityProbe.ProbeAsync(
            client,
            _options.OperatonBaseUrl,
            probeName: "WorkflowEngine",
            relativeProbePath: ProbePath,
            cancellationToken: cancellationToken);
    }
}
