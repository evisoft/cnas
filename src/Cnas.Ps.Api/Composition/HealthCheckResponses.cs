using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Custom JSON response writer for the <c>/health</c> endpoint.
/// Returns minimal status payload — no PII, no internal exception details (SEC 058).
/// </summary>
internal static class HealthCheckResponses
{
    /// <summary>
    /// Serializes the aggregated <see cref="HealthReport"/> as JSON. Per SEC 058 we deliberately
    /// suppress exception details — the caller gets a generic message plus a correlation id
    /// that ops can correlate to the structured log entry.
    /// </summary>
    public static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
