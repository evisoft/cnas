using System.Net.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Shared helper that performs a lightweight HTTP GET reachability probe against a
/// dependency's base URL. Centralised so every per-service <see cref="IHealthCheck"/>
/// adapter delegates to identical timeout, status-code, and exception-mapping rules.
/// </summary>
/// <remarks>
/// <para>
/// The probe uses a hard 2-second timeout (CTS-bounded) so a single slow upstream cannot
/// block the readiness endpoint. A short timeout is appropriate because the readiness
/// check itself runs on every load-balancer pass: a misbehaving dependency should fail
/// fast and let the orchestrator route traffic elsewhere.
/// </para>
/// <para>
/// Mapping rules:
/// <list type="bullet">
///   <item><c>null</c> / whitespace base URL → <see cref="HealthStatus.Degraded"/> ("not configured"). Local dev should not crash on missing MGov endpoints.</item>
///   <item>HTTP 2xx → <see cref="HealthStatus.Healthy"/>.</item>
///   <item>HTTP 4xx → <see cref="HealthStatus.Degraded"/> (upstream is reachable but the probe path itself is unauthorised or missing — the service is up, just not exposing <c>/health</c>).</item>
///   <item>HTTP 5xx → <see cref="HealthStatus.Unhealthy"/>.</item>
///   <item><see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>, <see cref="OperationCanceledException"/> → <see cref="HealthStatus.Unhealthy"/>.</item>
/// </list>
/// </para>
/// <para>
/// The probe path defaults to <c>/health</c>. Callers may override it (e.g. Operaton's
/// <c>/engine-rest/engine/</c> identity endpoint) by passing an explicit relative path.
/// </para>
/// </remarks>
public static class HttpReachabilityProbe
{
    /// <summary>Hard ceiling for a single readiness probe. Short on purpose — see remarks.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Executes the probe. See type-level remarks for the full mapping table.
    /// </summary>
    /// <param name="httpClient">Pre-configured <see cref="HttpClient"/>. Tests can substitute via <see cref="HttpMessageHandler"/>.</param>
    /// <param name="baseUrl">Dependency base URL (e.g. <c>https://msign.cnas.gov.md</c>). May be <c>null</c> / empty.</param>
    /// <param name="relativeProbePath">Relative path appended to <paramref name="baseUrl"/>. Defaults to <c>/health</c>.</param>
    /// <param name="probeName">Human-readable dependency name used in the <see cref="HealthCheckResult"/> description.</param>
    /// <param name="timeout">Optional override for <see cref="DefaultTimeout"/>; useful in tests.</param>
    /// <param name="cancellationToken">Cancellation token bound to the health-check framework's call.</param>
    /// <returns>Mapped <see cref="HealthCheckResult"/> per the type-level remarks.</returns>
    public static async Task<HealthCheckResult> ProbeAsync(
        HttpClient httpClient,
        string? baseUrl,
        string probeName,
        string relativeProbePath = "/health",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeName);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return HealthCheckResult.Degraded($"{probeName}: base URL not configured.");
        }

        // Build the probe URI defensively — let .NET surface a malformed base URL as a
        // degraded outcome rather than a thrown exception escaping the health-check pipeline.
        Uri probeUri;
        try
        {
            probeUri = new Uri(
                new Uri(baseUrl.TrimEnd('/') + "/"),
                relativeProbePath.TrimStart('/'));
        }
        catch (UriFormatException ex)
        {
            return HealthCheckResult.Degraded(
                $"{probeName}: malformed base URL '{baseUrl}'.", ex);
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status >= 200 && status < 300)
            {
                return HealthCheckResult.Healthy($"{probeName}: ok ({status}).");
            }
            if (status >= 400 && status < 500)
            {
                return HealthCheckResult.Degraded(
                    $"{probeName}: reachable but probe returned {status}.");
            }
            return HealthCheckResult.Unhealthy(
                $"{probeName}: upstream returned {status}.");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"{probeName}: transport failure.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Distinguish timeout (our linked CTS fired) from caller cancellation (re-throw).
            return HealthCheckResult.Unhealthy(
                $"{probeName}: timed out after {(timeout ?? DefaultTimeout).TotalSeconds:F1}s.", ex);
        }
    }
}
