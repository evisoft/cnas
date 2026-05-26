using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0529 / TOR CF 03.14 — universal report-export pipeline. A
// ReportExportInputDto carries the (already authorised) rows + column
// definitions; an IReportExporter implementation projects the input to the
// requested wire format (CSV / XLSX / DOCX / PDF). The DTOs themselves are
// classified Confidential because the row payload typically aggregates data
// the caller has already been authorised to see — they exit the system only
// behind the report-access policy on ReportsController.
//
// Contracts MUST NOT <see cref="…"/> into Cnas.Ps.Core per project rules.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0529 / TOR CF 03.14 — output format requested from
/// <c>IReportExportSelector</c>. Mirrors the wire vocabulary used by
/// <c>/api/reports/{code}/export?format=…</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate enum.</b> The legacy <see cref="ExportFormat"/> in
/// <c>SearchDto.cs</c> only defines CSV / XLSX / PDF — adding DOCX there
/// would silently affect every other consumer (grid exports, search exports)
/// that does not yet know how to render DOCX. A dedicated enum scopes the
/// breaking change to the report-export pipeline.
/// </para>
/// <para>
/// Underlying integer values are deliberately stable so request loggers and
/// audit rows that record the format as an int survive enum additions.
/// </para>
/// </remarks>
public enum ReportExportFormat
{
    /// <summary>Comma-separated values (RFC 4180), UTF-8 with BOM.</summary>
    Csv = 0,

    /// <summary>OOXML spreadsheet (.xlsx) rendered with <c>ClosedXML</c>.</summary>
    Xlsx = 1,

    /// <summary>OOXML word-processing document (.docx) rendered with <c>DocumentFormat.OpenXml</c>.</summary>
    Docx = 2,

    /// <summary>Portable Document Format rendered with <c>QuestPDF</c>.</summary>
    Pdf = 3,
}

/// <summary>
/// R0529 — single column definition for the universal report-export pipeline.
/// </summary>
/// <param name="Header">
/// Already-localised header text shown to the end user (e.g. <c>"Cod"</c> /
/// <c>"Code"</c>). Caller is responsible for locale resolution.
/// </param>
/// <param name="Width">
/// Optional preferred column width hint. Rendered as a percentage of the
/// page width by exporters that honour widths (XLSX, DOCX, PDF). Null leaves
/// the renderer free to auto-fit. Range 0.0–1.0 exclusive at validation.
/// </param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record ReportExportColumnDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Header,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    double? Width = null);

/// <summary>
/// R0529 — declarative input handed to <see cref="ReportExportColumnDto"/>-aware
/// exporters. The payload is Confidential by class-level annotation because
/// the row matrix typically aggregates already-authorised data; routes
/// returning this DTO therefore raise the corresponding sensitivity header
/// (<c>X-CNAS-Sensitivity: Confidential</c>) via the platform middleware.
/// </summary>
/// <param name="ReportTitle">
/// Document title rendered on the first sheet name (XLSX), the title
/// paragraph (DOCX), or the page header (PDF). Length 1..256 enforced at
/// validation; the renderer truncates further to respect format-specific
/// limits (XLSX sheet name = 31 chars).
/// </param>
/// <param name="Columns">
/// Ordered column definitions; the renderer emits columns in exactly this
/// order. Validation rejects empty lists and lists with more than 100
/// entries (defence against amplification attacks that try to widen the
/// matrix beyond what any reasonable report needs).
/// </param>
/// <param name="Rows">
/// Materialised cell matrix. Each row is a list of cell strings whose length
/// MUST equal <see cref="Columns"/>.Count. Validation rejects matrices with
/// more than 100_000 rows (DOS protection — anything larger should run as a
/// background job per R0252 / TOR PSR 010).
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "Row payload typically aggregates authorised data; carry the Confidential header on the response.")]
public sealed record ReportExportInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ReportTitle,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<ReportExportColumnDto> Columns,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// R0529 — success envelope returned by every <c>IReportExporter</c>
/// implementation. Carries the rendered bytes plus the wire metadata the
/// controller needs to set on the <c>FileResult</c>.
/// </summary>
/// <param name="Bytes">
/// Raw exported bytes. Validation ensures the array is non-null and
/// non-empty before the controller wraps it in a <c>FileContentResult</c>.
/// </param>
/// <param name="ContentType">
/// Canonical MIME type matching <paramref name="Format"/>:
/// <list type="bullet">
///   <item><c>text/csv; charset=utf-8</c> for CSV</item>
///   <item><c>application/vnd.openxmlformats-officedocument.spreadsheetml.sheet</c> for XLSX</item>
///   <item><c>application/vnd.openxmlformats-officedocument.wordprocessingml.document</c> for DOCX</item>
///   <item><c>application/pdf</c> for PDF</item>
/// </list>
/// </param>
/// <param name="Format">The format selected by the caller, echoed back for telemetry.</param>
/// <param name="FileExtension">
/// Dotted file extension including the leading dot (e.g. <c>.xlsx</c>). The
/// controller uses this to build the <c>Content-Disposition</c> filename.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "Rendered bytes encode the full row payload; treat as Confidential at the boundary.")]
public sealed record ReportExportResultDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    byte[] Bytes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContentType,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    ReportExportFormat Format,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string FileExtension);
