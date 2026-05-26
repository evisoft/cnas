using System.Net.Http;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Readiness probe for the MDocs managed-document-storage MGov platform service.
/// </summary>
/// <remarks>
/// The <c>MDocs*</c> properties on <see cref="MGovOptions"/> default to empty strings;
/// when unconfigured the probe returns <see cref="HealthStatus.Degraded"/> rather than
/// <see cref="HealthStatus.Unhealthy"/> so local-dev environments without an MDocs
/// endpoint can still bring up the API.
/// </remarks>
/// <param name="httpClientFactory">Factory used to obtain a short-lived probe client.</param>
/// <param name="options">Bound MGov configuration snapshot.</param>
public sealed class MDocsHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<MGovOptions> options) : IHealthCheck
{
    /// <summary>Probe path tag — used for structured logging and tag-based filtering.</summary>
    private const string ProbePathTag = "mgov.mdocs.ready";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly MGovOptions _options = options.Value;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(ProbePathTag);
        return HttpReachabilityProbe.ProbeAsync(
            client,
            _options.MDocsBaseUrl,
            probeName: "MDocs",
            cancellationToken: cancellationToken);
    }
}
