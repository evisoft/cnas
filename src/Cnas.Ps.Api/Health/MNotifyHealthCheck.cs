using System.Net.Http;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Readiness probe for the MNotify citizen-notification MGov platform service.
/// </summary>
/// <param name="httpClientFactory">Factory used to obtain a short-lived probe client.</param>
/// <param name="options">Bound MGov configuration snapshot.</param>
public sealed class MNotifyHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<MGovOptions> options) : IHealthCheck
{
    /// <summary>Probe path tag — used for structured logging and tag-based filtering.</summary>
    private const string ProbePathTag = "mgov.mnotify.ready";

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
            _options.MNotifyBaseUrl,
            probeName: "MNotify",
            cancellationToken: cancellationToken);
    }
}
