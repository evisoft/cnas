namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Report — configurable report definition (UC19/UC09). TOR §2.3 #7.
/// </summary>
/// <remarks>
/// <para>
/// The definition is data-driven (FLEX 003): code path doesn't change when business adds
/// or modifies a report. Generated artifacts live in MinIO; this entity holds the recipe.
/// </para>
/// <para>
/// <b>R1900-R1905 (iter-145).</b> The catalog metadata block (<see cref="NameRo"/> /
/// <see cref="Purpose"/> / <see cref="Audience"/> / <see cref="Frequency"/> /
/// <see cref="ColumnsJson"/> / <see cref="RbacRole"/> / <see cref="Schedule"/> /
/// <see cref="OutputFormatsJson"/>) is seeded by
/// <c>IReportCatalogSeedService</c> from the in-code Annex 6 descriptor table so the
/// catalog endpoint can render purpose, audience, frequency, RBAC and schedule per row
/// without round-tripping to the materialiser. Older rows seeded before iter-145 carry
/// empty defaults that the seeder upgrades on the next refresh.
/// </para>
/// </remarks>
public sealed class Report : AuditableEntity
{
    /// <summary>Stable code used by automations and API callers.</summary>
    public required string Code { get; set; }

    /// <summary>Human-readable name shown in dashboards.</summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// SQL or query DSL used to materialise the dataset. Parameters are passed as a JSON
    /// object — never concatenated into the query (CLAUDE.md SEC §5.5).
    /// </summary>
    public required string QueryTemplate { get; set; }

    /// <summary>JSON-schema describing the report's parameters.</summary>
    public string ParameterSchemaJson { get; set; } = "{}";

    /// <summary>Default output format: <c>pdf</c>, <c>xlsx</c>, or <c>csv</c>.</summary>
    public string DefaultFormat { get; set; } = "pdf";

    /// <summary>True if the report is exposed to anonymous Internet users (UC01).</summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// R1901 — Romanian display name (canonical UI language).
    /// Empty by default; populated by the catalog seeder.
    /// </summary>
    public string NameRo { get; set; } = string.Empty;

    /// <summary>
    /// R1901 — short purpose / decision-support statement
    /// ("Why does this report exist?"). Free-form Romanian prose.
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// R1901 — intended audience label
    /// (e.g. <c>cnas-decider</c>, <c>cnas-admin</c>, <c>auditor</c>, <c>statistician</c>).
    /// Free-form to allow operational categorisation beyond role codes.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// R1901 — production cadence — one of <c>OnDemand</c>, <c>Daily</c>,
    /// <c>Weekly</c>, <c>Monthly</c>, <c>Quarterly</c>, <c>Annual</c>.
    /// </summary>
    public string Frequency { get; set; } = "OnDemand";

    /// <summary>
    /// R1901 — JSON array of column descriptors materialised by the report
    /// (e.g. <c>[{"name":"DossierSqid","type":"string"}]</c>). Used by the
    /// catalog UI to render the schema preview without invoking the report.
    /// </summary>
    public string ColumnsJson { get; set; } = "[]";

    /// <summary>
    /// R1901 — primary RBAC role code (see <see cref="Cnas.Ps.Core.Common.RoleCodes"/>)
    /// authorised to generate this report. Multi-role gating is layered on top in
    /// the application service.
    /// </summary>
    public string RbacRole { get; set; } = "cnas-admin";

    /// <summary>
    /// R1905 — schedule expression (Quartz-compatible cron or sentinel
    /// <c>OnDemand</c>). Powers the Annex 6 schedule registry.
    /// </summary>
    public string Schedule { get; set; } = "OnDemand";

    /// <summary>
    /// R1901 — JSON array of supported output formats
    /// (e.g. <c>["csv","xlsx","pdf"]</c>). Distinct from <see cref="DefaultFormat"/>
    /// which is the format selected when the caller does not override.
    /// </summary>
    public string OutputFormatsJson { get; set; } = "[\"csv\"]";

    /// <summary>
    /// R1902 — category label (e.g. <c>PayerRevenues</c>, <c>Contributions</c>,
    /// <c>DecisionsIssued</c>, <c>PaymentsProcessed</c>, <c>Statistical</c>,
    /// <c>AuditSecurity</c>, <c>PerformanceKpi</c>, <c>EessiCompliance</c>).
    /// </summary>
    public string Category { get; set; } = "Statistical";
}
