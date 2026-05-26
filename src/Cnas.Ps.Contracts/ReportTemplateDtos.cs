namespace Cnas.Ps.Contracts;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — wire DTO for one ad-hoc report template. The
/// matching <c>IReportEngine</c> runs the template against the named registry and
/// returns paged result rows (<see cref="ReportExecutionResultDto"/>) plus an
/// export-byte payload via the R0226 universal grid exporter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid invariant.</b> Identifiers are Sqid-encoded strings per CLAUDE.md RULE 3 —
/// the raw <see cref="long"/> primary keys never leave the system.
/// </para>
/// <para>
/// <b>Stability.</b> Field names and the operator/direction literals are part of the
/// public API contract — renaming is a breaking change.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded id of the report template.</param>
/// <param name="Code">
/// Kebab-case stable identifier (e.g. <c>report.solicitants.active-by-region</c>).
/// Validator enforces <c>^[a-z][a-z0-9.-]{2,127}$</c>.
/// </param>
/// <param name="Name">User-supplied display label.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="Registry">
/// Stable registry code identifying the QBE schema to query (e.g. <c>Solicitant</c>).
/// </param>
/// <param name="SelectedFields">Ordered list of field names projected onto each row.</param>
/// <param name="Filter">QBE filter envelope applied before the budget guard.</param>
/// <param name="Ordering">Multi-column ordering specifications (≤ 5 entries).</param>
/// <param name="GroupByField">
/// Optional single field used to group result rows; when supplied the engine emits
/// one row per distinct value with a <c>count</c> aggregate column. Must also appear
/// in <see cref="SelectedFields"/>.
/// </param>
/// <param name="OwnerUserSqid">Sqid-encoded id of the owning user.</param>
/// <param name="IsShared">
/// When <c>true</c>, any caller with the report-view role may execute and export
/// this template. The owner remains the sole mutator.
/// </param>
public sealed record ReportTemplateDto(
    string Id,
    string Code,
    string Name,
    string? Description,
    string Registry,
    IReadOnlyList<string> SelectedFields,
    QbeFilterDto Filter,
    IReadOnlyList<ReportOrderingDto> Ordering,
    string? GroupByField,
    string OwnerUserSqid,
    bool IsShared);

/// <summary>
/// R0156 — one ordering specification on a <see cref="ReportTemplateDto"/>. Direction
/// is the stable PascalCase string <c>"ASC"</c> or <c>"DESC"</c> so the wire contract
/// survives any future server-side renaming.
/// </summary>
/// <param name="Field">Field name to order by; must appear in the registry schema.</param>
/// <param name="Direction">
/// Either <c>"ASC"</c> or <c>"DESC"</c> (case-insensitive on input; canonical
/// uppercase on output).
/// </param>
public sealed record ReportOrderingDto(string Field, string Direction)
{
    /// <summary>The canonical ascending literal.</summary>
    public const string Asc = "ASC";

    /// <summary>The canonical descending literal.</summary>
    public const string Desc = "DESC";
}

/// <summary>
/// R0156 — request body for <c>POST /api/reports/templates</c>. The owner is the
/// authenticated caller — the input DTO deliberately does NOT carry an
/// <c>OwnerUserSqid</c> field so a non-admin caller cannot forge a template for
/// someone else (mass-assignment protection per CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="Code">Kebab-case stable identifier.</param>
/// <param name="Name">Display label.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="Registry">Registry code targeted by the template.</param>
/// <param name="SelectedFields">Ordered list of field names to project.</param>
/// <param name="Filter">QBE filter envelope (use empty for no narrowing).</param>
/// <param name="Ordering">Ordering specifications (≤ 5 entries).</param>
/// <param name="GroupByField">Optional group-by field (must appear in <see cref="SelectedFields"/>).</param>
/// <param name="IsShared">When <c>true</c>, the template is visible to every authenticated CNAS user.</param>
public sealed record ReportTemplateCreateDto(
    string Code,
    string Name,
    string? Description,
    string Registry,
    IReadOnlyList<string> SelectedFields,
    QbeFilterDto Filter,
    IReadOnlyList<ReportOrderingDto> Ordering,
    string? GroupByField,
    bool IsShared);

/// <summary>
/// R0156 — request body for <c>PUT /api/reports/templates/{sqid}</c>. Updates every
/// mutable field on a template. The registry is intentionally NOT mutable after
/// create — re-pointing a template at a different registry would invalidate every
/// previously-saved selected-field reference.
/// </summary>
/// <param name="Name">New display label.</param>
/// <param name="Description">New optional description.</param>
/// <param name="SelectedFields">New projection list.</param>
/// <param name="Filter">New QBE filter envelope.</param>
/// <param name="Ordering">New ordering specifications.</param>
/// <param name="GroupByField">New optional group-by field.</param>
/// <param name="IsShared">New sharing flag.</param>
public sealed record ReportTemplateUpdateDto(
    string Name,
    string? Description,
    IReadOnlyList<string> SelectedFields,
    QbeFilterDto Filter,
    IReadOnlyList<ReportOrderingDto> Ordering,
    string? GroupByField,
    bool IsShared);

/// <summary>
/// R0156 — successful response from <c>POST /api/reports/templates/{sqid}/run</c>.
/// Carries the column definitions, the paged rows, the total matching row count
/// (sourced from the R0167 query-budget verdict), and the wall-clock duration.
/// </summary>
/// <param name="Columns">
/// Ordered column definitions. The list mirrors <see cref="ReportTemplateDto.SelectedFields"/>
/// (or carries a single column named after <see cref="ReportTemplateDto.GroupByField"/>
/// plus a synthetic <c>"count"</c> column when grouping is active).
/// </param>
/// <param name="Rows">The materialised rows for the requested page.</param>
/// <param name="TotalRowCount">Total matching row count BEFORE paging.</param>
/// <param name="ElapsedMs">Engine wall-clock duration in milliseconds.</param>
public sealed record ReportExecutionResultDto(
    IReadOnlyList<string> Columns,
    IReadOnlyList<ReportRowDto> Rows,
    int TotalRowCount,
    int ElapsedMs);

/// <summary>
/// R0156 — one materialised row in a <see cref="ReportExecutionResultDto"/>.
/// </summary>
/// <param name="Cells">
/// Map from column name (matching <see cref="ReportExecutionResultDto.Columns"/>) to
/// the raw cell value. Cell value types follow the underlying entity property type:
/// strings, integers, decimals, dates, booleans, or null when the value is absent.
/// </param>
public sealed record ReportRowDto(IReadOnlyDictionary<string, object?> Cells);
