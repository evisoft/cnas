using System.Net.Http;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Readiness probe for the MSign (qualified e-signature) MGov platform service.
/// Delegates to <see cref="HttpReachabilityProbe"/> against
/// <see cref="MGovOptions.MSignBaseUrl"/>. An empty base URL is treated as degraded
/// (local-dev safety) — matching the matching behaviour of <see cref="MSignClient"/>.
/// </summary>
/// <param name="httpClientFactory">Factory used to obtain a short-lived probe client.</param>
/// <param name="options">Bound MGov configuration snapshot.</param>
public sealed class MSignHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<MGovOptions> options) : IHealthCheck
{
    /// <summary>Probe path tag — used for structured logging and tag-based filtering.</summary>
    private const string ProbePathTag = "mgov.msign.ready";

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
            _options.MSignBaseUrl,
            probeName: "MSign",
            cancellationToken: cancellationToken);
    }
}
