using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1900-R1905 / iter-145 — Report Catalog DTOs. Contracts MUST NOT use
// <see cref="..."/> references into Cnas.Ps.Core — they live in a leaf
// assembly and only depend on themselves + Contracts.Security. All Id fields
// are Sqid-encoded per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1900-R1905 / TOR §13 Annex 6 — outbound projection of one report-catalog
/// row. Carries every metadata block specified by R1901: Code, NameRo,
/// Purpose, Audience, Frequency, ParametersJson, ColumnsJson, RbacRole,
/// Schedule, OutputFormatsJson. The materialiser is invoked separately via
/// the report-generation endpoint — this DTO documents the recipe, not the
/// dataset.
/// </summary>
/// <param name="Id">Sqid-encoded report id.</param>
/// <param name="Code">Stable report code (e.g. <c>RPT-PEN-ACTIVE</c>).</param>
/// <param name="NameRo">Romanian display name (canonical UI language).</param>
/// <param name="Purpose">Short purpose / decision-support statement.</param>
/// <param name="Audience">Intended audience label (decider, admin, auditor, statistician).</param>
/// <param name="Frequency">Production cadence (OnDemand / Daily / Weekly / Monthly / Quarterly / Annual).</param>
/// <param name="ParametersJson">JSON schema describing accepted parameters.</param>
/// <param name="ColumnsJson">JSON array of column descriptors materialised by the report.</param>
/// <param name="RbacRole">Primary RBAC role authorised to generate this report.</param>
/// <param name="Schedule">Quartz-compatible cron expression or <c>OnDemand</c>.</param>
/// <param name="OutputFormatsJson">JSON array of supported export formats.</param>
/// <param name="Category">High-level category (R1902).</param>
/// <param name="DefaultFormat">Default output format when caller does not override.</param>
/// <param name="IsPublic">True when the report is exposed to anonymous Internet users.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ReportCatalogRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string NameRo,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Purpose,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Audience,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Frequency,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ParametersJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ColumnsJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RbacRole,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Schedule,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string OutputFormatsJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Category,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DefaultFormat,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsPublic);

/// <summary>
/// R1900-R1905 — paged envelope returned by the report-catalog list endpoint.
/// </summary>
/// <param name="Items">Catalog rows on the requested page, ordered by <c>Code</c>.</param>
/// <param name="Total">Total catalog rows matching the filter (across all pages).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ReportCatalogPageDto(
    IReadOnlyList<ReportCatalogRowDto> Items,
    int Total);

/// <summary>
/// R1900-R1905 — outcome envelope returned by the catalog-refresh endpoint.
/// Tells the operator how many rows were inserted vs upserted vs unchanged
/// during a refresh run. The refresh is idempotent: a re-run that finds
/// every row already aligned reports zero inserts / upserts and a non-zero
/// <see cref="Unchanged"/> total.
/// </summary>
/// <param name="Inserted">Rows inserted because no row with the descriptor's code existed.</param>
/// <param name="Updated">Rows upserted because the existing row's metadata drifted from the descriptor.</param>
/// <param name="Unchanged">Rows skipped because the existing row already matched the descriptor.</param>
/// <param name="Total">Total descriptors processed (<c>= Inserted + Updated + Unchanged</c>).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record ReportCatalogRefreshResultDto(
    int Inserted,
    int Updated,
    int Unchanged,
    int Total);
