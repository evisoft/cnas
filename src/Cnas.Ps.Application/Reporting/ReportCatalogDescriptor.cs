namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1900-R1905 / TOR §13 Annex 6 — immutable descriptor for one Annex 6
/// report. Held in <see cref="ReportCatalogDescriptors"/> and consumed by
/// <c>IReportCatalogSeedService</c> to seed / refresh the persisted catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a value-type record.</b> The descriptor is pure data — no
/// behaviour, no dependencies, no I/O. Keeping it as a sealed record makes
/// the in-code seed table trivially testable (reference equality + value
/// equality both work) and keeps the seeder a one-pass loop with no
/// reflection.
/// </para>
/// <para>
/// <b>JSON payloads.</b> <see cref="ParametersJson"/>,
/// <see cref="ColumnsJson"/> and <see cref="OutputFormatsJson"/> are stored
/// verbatim. The descriptor authors are responsible for emitting valid JSON;
/// the seeder does not parse them — they are persisted as opaque blobs and
/// served via the catalog endpoint to drive UI rendering.
/// </para>
/// </remarks>
/// <param name="Code">Stable report code (e.g. <c>RPT-PEN-ACTIVE</c>).</param>
/// <param name="NameRo">Romanian display name (canonical UI language).</param>
/// <param name="Purpose">Short purpose / decision-support statement.</param>
/// <param name="Audience">Intended audience label.</param>
/// <param name="Frequency">Production cadence (OnDemand / Daily / Weekly / Monthly / Quarterly / Annual).</param>
/// <param name="ParametersJson">JSON schema describing accepted parameters.</param>
/// <param name="ColumnsJson">JSON array of column descriptors.</param>
/// <param name="RbacRole">Primary RBAC role authorised to generate this report.</param>
/// <param name="Schedule">Quartz-compatible cron expression or <c>OnDemand</c>.</param>
/// <param name="OutputFormatsJson">JSON array of supported export formats.</param>
/// <param name="Category">High-level category (R1902).</param>
/// <param name="DefaultFormat">Default output format.</param>
public sealed record ReportCatalogDescriptor(
    string Code,
    string NameRo,
    string Purpose,
    string Audience,
    string Frequency,
    string ParametersJson,
    string ColumnsJson,
    string RbacRole,
    string Schedule,
    string OutputFormatsJson,
    string Category,
    string DefaultFormat);
