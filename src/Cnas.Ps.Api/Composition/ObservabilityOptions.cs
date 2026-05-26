namespace Cnas.Ps.Api.Composition;

/// <summary>
/// Strongly-typed configuration for the CNAS OpenTelemetry pipeline, bound from
/// <c>Cnas:Observability</c> in app settings. Per CLAUDE.md Phase 6 ("structured
/// logging, key-value pairs, correlation IDs"), the system must export traces,
/// metrics, and logs via OTLP so SREs can stitch a single request across the
/// MGov platform without manually correlating log lines.
/// </summary>
/// <remarks>
/// <para>
/// The OTLP endpoint is intentionally optional. When <see cref="OtlpEndpoint"/>
/// is null or whitespace the exporter is not registered, which makes
/// <see cref="ApiCompositionRoot.AddCnasObservability(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration)"/>
/// a no-op in development and unit tests — no background flush threads, no
/// retry loops, no network traffic. Production simply sets the endpoint and
/// the SDK starts exporting.
/// </para>
/// <para>
/// Resource attributes (<c>service.name</c>, <c>service.version</c>,
/// <c>deployment.environment</c>) are baked into every emitted signal so the
/// collector can route by service and dashboards can filter by environment
/// without operators tagging every metric.
/// </para>
/// </remarks>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// Configuration section name. Bind via
    /// <c>configuration.GetSection(ObservabilityOptions.SectionName)</c>.
    /// </summary>
    public const string SectionName = "Cnas:Observability";

    /// <summary>
    /// OTLP/gRPC endpoint (typically <c>http://otel-collector:4317</c>).
    /// Empty or null disables exporter registration entirely so apps run
    /// unchanged in dev/test. Stable production setting — changing it
    /// re-routes every signal to a new collector.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// <c>service.name</c> resource attribute reported on every span,
    /// metric, and log record. Defaults to <c>cnas-ps-api</c>. Dashboards
    /// pin against this name, so do not change it after launch without a
    /// migration plan.
    /// </summary>
    public string ServiceName { get; set; } = "cnas-ps-api";

    /// <summary>
    /// <c>service.version</c> resource attribute. Useful for canary analysis
    /// — when v1.1.0 starts emitting more errors than v1.0.0, the operator
    /// can roll back. Defaults to <c>1.0.0</c>.
    /// </summary>
    public string ServiceVersion { get; set; } = "1.0.0";

    /// <summary>
    /// <c>deployment.environment</c> resource attribute
    /// (<c>development</c> | <c>staging</c> | <c>production</c>). Drives
    /// dashboard filters and PagerDuty routing rules. Defaults to
    /// <c>development</c>.
    /// </summary>
    public string Environment { get; set; } = "development";

    /// <summary>
    /// When true, additionally registers a console exporter for traces and
    /// metrics. Intended for local debugging only — produces a lot of
    /// stdout noise and is never enabled in production.
    /// </summary>
    public bool EnableConsoleExporter { get; set; }
}
