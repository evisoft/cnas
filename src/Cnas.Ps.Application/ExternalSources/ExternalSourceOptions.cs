namespace Cnas.Ps.Application.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — bound options envelope for the per-source external
/// ingestion adapters. The root section is <c>Cnas:ExternalSources</c>; one
/// child section per source code mirrors the deployment model where each
/// source has independent credentials and base URLs.
/// </summary>
public sealed class ExternalSourceOptions
{
    /// <summary>Well-known configuration section name (<c>Cnas:ExternalSources</c>).</summary>
    public const string SectionName = "Cnas:ExternalSources";

    /// <summary>RSP (Registrul de Stat al Populației) connector options.</summary>
    public ExternalSourceConnectorOptions Rsp { get; set; } = new();

    /// <summary>RSUD (Registrul de Stat al Unităților de Drept) connector options.</summary>
    public ExternalSourceConnectorOptions Rsud { get; set; } = new();

    /// <summary>SFS (Serviciul Fiscal de Stat) connector options.</summary>
    public ExternalSourceConnectorOptions Sfs { get; set; } = new();
}

/// <summary>
/// Per-source configuration envelope. Each external source ships with its own
/// base URL; the placeholder connectors return a deterministic failure when
/// the base URL is blank so production deployments fail loudly.
/// </summary>
public sealed class ExternalSourceConnectorOptions
{
    /// <summary>
    /// Base URL of the upstream source. Blank by default — the placeholder
    /// connector returns the source-specific NOT_CONFIGURED failure when
    /// blank so production deployments cannot silently no-op.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}
