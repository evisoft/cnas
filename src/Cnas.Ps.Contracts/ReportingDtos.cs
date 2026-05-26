namespace Cnas.Ps.Contracts;

/// <summary>
/// One entry in the report catalog exposed by <c>GET /api/reports</c>. The
/// <see cref="Code"/> is the stable, machine-readable identifier accepted by
/// <c>POST /api/reports/{code}/generate</c>; the title fields carry the
/// human-readable label in each of the three CNAS-supported UI languages
/// (Romanian, Russian, English).
/// </summary>
/// <remarks>
/// The titles default to the code itself for catalog entries that have not yet been
/// translated — the API contract guarantees that all three fields are non-null so
/// the front-end can render any one of them without a fallback branch on every
/// row. Translations are added as the i18n team finalises the wording; existing
/// entries default to the code so the catalog endpoint never reveals an "(empty)"
/// row.
/// </remarks>
/// <param name="Code">
/// Stable report code (e.g. <c>AUDIT_LOG</c>, <c>RPT-PEN-ACTIVE</c>). The code is
/// part of the API contract — renaming a code is a breaking change. Codes are
/// case-sensitive.
/// </param>
/// <param name="TitleRo">Romanian title (default UI language).</param>
/// <param name="TitleRu">Russian title.</param>
/// <param name="TitleEn">English title.</param>
public sealed record ReportCatalogEntryOutput(
    string Code,
    string TitleRo,
    string TitleRu,
    string TitleEn);

/// <summary>
/// Request body for <c>POST /api/reports/{code}/generate</c>. Carries the optional
/// parameter dictionary forwarded to the report materialiser plus the desired output
/// format. The <c>code</c> itself lives on the route, not in this payload.
/// </summary>
/// <remarks>
/// <para>
/// The parameter dictionary is shape-agnostic on purpose: every report defines its
/// own parameter schema (e.g. <c>fromUtc</c>, <c>toUtc</c>, <c>maxRows</c>, <c>asOfUtc</c>,
/// <c>passportCode</c>) and the underlying <see cref="ReportCatalogEntryOutput.Code"/>
/// determines which keys are honoured. Unknown keys are ignored by the
/// materialiser rather than rejected at the boundary — this lets the front-end
/// safely send a superset of params for the common scenarios (e.g. always pass
/// <c>asOfUtc</c>) without 400-ing reports that do not declare it.
/// </para>
/// <para>
/// Keys are case-sensitive and values are serialised as strings on the wire; the
/// service parses them with the same JSON-aware readers (<c>ReadUtcDate</c>,
/// <c>ReadInt</c>, <c>ReadString</c>) that the inline <c>parametersJson</c>
/// surface uses.
/// </para>
/// </remarks>
/// <param name="Parameters">
/// Per-report parameter map; null is treated as an empty map (no parameters).
/// </param>
/// <param name="Format">Desired output format (CSV / XLSX / PDF).</param>
public sealed record ReportGenerateRequest(
    Dictionary<string, string?>? Parameters,
    ExportFormat Format);
