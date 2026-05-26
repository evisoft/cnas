using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — XLSX implementation of <see cref="IReportExporter"/>
/// backed by <c>ClosedXML</c> (already pinned in <c>Directory.Packages.props</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> OOXML SpreadsheetML — one worksheet named after the
/// report title (truncated to Excel's 31-char limit and stripped of the six
/// illegal characters). Row 1 carries the headers in bold; subsequent rows
/// carry the cell text verbatim. Column widths are derived from the
/// <see cref="ReportExportColumnDto.Width"/> hint (interpreted as a fraction
/// of the page width — multiplied by a fixed 80-char baseline) when supplied;
/// columns without a hint use ClosedXML's <c>AdjustToContents</c>.
/// </para>
/// <para>
/// <b>Stateless + thread-safe.</b> Per-call <see cref="XLWorkbook"/> means
/// concurrent invocations never share workbook state.
/// </para>
/// </remarks>
public sealed class XlsxReportExporter : IReportExporter
{
    /// <summary>Canonical MIME type for the .xlsx wire format.</summary>
    internal const string XlsxMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Dotted file extension for the .xlsx wire format.</summary>
    internal const string XlsxExtension = ".xlsx";

    /// <summary>
    /// Baseline column-width unit used when honouring a fractional
    /// <see cref="ReportExportColumnDto.Width"/> hint. Approximates a
    /// readable page width in ClosedXML width units (1 unit ≈ 1 char of the
    /// default font).
    /// </summary>
    private const double WidthBaseline = 80.0;

    /// <inheritdoc />
    public ReportExportFormat Format => ReportExportFormat.Xlsx;

    /// <inheritdoc />
    public Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(TruncateSheetName(input.ReportTitle));

        // Header row.
        for (int c = 0; c < input.Columns.Count; c++)
        {
            var col = input.Columns[c];
            ws.Cell(1, c + 1).Value = col.Header;
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            if (col.Width.HasValue && col.Width.Value > 0.0 && col.Width.Value < 1.0)
            {
                ws.Column(c + 1).Width = col.Width.Value * WidthBaseline;
            }
        }

        // Data rows — text-only cells (the input DTO carries pre-formatted strings).
        for (int r = 0; r < input.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = input.Rows[r];
            for (int c = 0; c < input.Columns.Count; c++)
            {
                // Defensive: validator guarantees row.Count == Columns.Count, but a
                // null cell could still slip through (validator does not reject nulls).
                var cell = c < row.Count ? row[c] ?? string.Empty : string.Empty;
                ws.Cell(r + 2, c + 1).Value = cell;
            }
        }

        // Auto-fit any columns that did not receive an explicit width hint.
        for (int c = 0; c < input.Columns.Count; c++)
        {
            if (!input.Columns[c].Width.HasValue)
            {
                ws.Column(c + 1).AdjustToContents();
            }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var resultValue = new ReportExportResultDto(
            Bytes: ms.ToArray(),
            ContentType: XlsxMimeType,
            Format: ReportExportFormat.Xlsx,
            FileExtension: XlsxExtension);
        return Task.FromResult(Result<ReportExportResultDto>.Success(resultValue));
    }

    /// <summary>
    /// Truncates and sanitises <paramref name="name"/> for use as an Excel
    /// sheet name (31-char max; the six characters <c>\ / ? * [ ]</c> plus
    /// <c>:</c> are illegal).
    /// </summary>
    /// <param name="name">Caller-supplied sheet title.</param>
    /// <returns>An Excel-legal sheet name.</returns>
    internal static string TruncateSheetName(string name)
    {
        var sanitised = new string((name ?? "Report")
            .Select(c => c is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : c).ToArray());
        if (sanitised.Length > 31)
        {
            sanitised = sanitised[..31];
        }
        if (string.IsNullOrWhiteSpace(sanitised))
        {
            sanitised = "Report";
        }
        return sanitised;
    }
}
