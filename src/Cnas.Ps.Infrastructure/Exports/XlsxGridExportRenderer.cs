using System.Globalization;
using ClosedXML.Excel;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Exports;

/// <summary>
/// R0226 / TOR UI 013 — XLSX implementation of <see cref="IGridExportRenderer"/>
/// backed by <c>ClosedXML</c> (already pinned in
/// <c>Directory.Packages.props</c>, no new NuGet dependency).
/// </summary>
/// <remarks>
/// <para>
/// <b>Native typed cells.</b> The renderer projects each cell value to the
/// appropriate native XLSX cell type so spreadsheet consumers (Excel pivot
/// tables, Power BI imports) can sort and filter correctly. Dates and
/// datetimes become typed Excel datetime cells with the
/// <c>"yyyy-mm-dd hh:mm:ss"</c> format applied; booleans become Excel BOOLEAN
/// cells with locale-localised display strings written into the text override
/// (the binary cell value remains the typed boolean for spreadsheet
/// downstream tooling).
/// </para>
/// <para>
/// <b>Stateless + thread-safe.</b> ClosedXML's <see cref="XLWorkbook"/> is
/// created per call so concurrent renders never share workbook state.
/// </para>
/// </remarks>
public sealed class XlsxGridExportRenderer : IGridExportRenderer
{
    /// <summary>OOXML MIME type for the .xlsx file extension.</summary>
    internal const string XlsxMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Clock abstraction used to stamp the suggested filename.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>
    /// Builds an XLSX renderer with the supplied clock.
    /// </summary>
    /// <param name="clock">Clock abstraction used to stamp the suggested filename.</param>
    public XlsxGridExportRenderer(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Parameterless convenience constructor that wires
    /// <see cref="SystemTimeProvider"/> — used by unit tests that don't care
    /// about the filename timestamp value.
    /// </summary>
    public XlsxGridExportRenderer() : this(new SystemTimeProvider()) { }

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Xlsx;

    /// <inheritdoc />
    public Task<Result<GridExportResult>> RenderAsync(
        GridExportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        using var wb = new XLWorkbook();
        var sheetName = TruncateSheetName(request.Title ?? request.GridName);
        var ws = wb.Worksheets.Add(sheetName);

        // Header row in column-order.
        for (int c = 0; c < request.Columns.Count; c++)
        {
            ws.Cell(1, c + 1).Value = request.Columns[c].Header;
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        // Data rows — typed projection per column data type.
        for (int r = 0; r < request.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = request.Rows[r];
            for (int c = 0; c < request.Columns.Count; c++)
            {
                var column = request.Columns[c];
                row.Cells.TryGetValue(column.FieldName, out var raw);
                WriteCell(ws.Cell(r + 2, c + 1), raw, column.DataType, request.Language);
            }
        }

        ws.Columns().AdjustToContents();

        // Optional footer note — placed two rows below the last data row in
        // bold italics so spreadsheet readers see the provenance without
        // confusing it for a data row.
        if (!string.IsNullOrWhiteSpace(request.FooterNote))
        {
            var footerRow = request.Rows.Count + 3;
            var footerCell = ws.Cell(footerRow, 1);
            footerCell.Value = request.FooterNote;
            footerCell.Style.Font.Italic = true;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var fileName = SuggestFileName(request.GridName, _clock.UtcNow);
        var resultValue = new GridExportResult(
            Content: ms.ToArray(),
            ContentType: XlsxMimeType,
            SuggestedFileName: fileName);
        return Task.FromResult(Result<GridExportResult>.Success(resultValue));
    }

    /// <summary>
    /// Writes <paramref name="raw"/> into <paramref name="cell"/> using the
    /// appropriate native XLSX cell type for <paramref name="type"/>. Null
    /// values leave the cell empty.
    /// </summary>
    /// <param name="cell">Target cell.</param>
    /// <param name="raw">Raw cell value.</param>
    /// <param name="type">Column data type.</param>
    /// <param name="language">Locale code (for boolean localisation).</param>
    internal static void WriteCell(IXLCell cell, object? raw, GridColumnDataType type, string language)
    {
        if (raw is null)
        {
            cell.Clear();
            return;
        }

        switch (type)
        {
            case GridColumnDataType.Date:
                if (raw is DateTime d)
                {
                    cell.Value = d.Date;
                    cell.Style.DateFormat.Format = "yyyy-mm-dd";
                    return;
                }
                break;

            case GridColumnDataType.DateTime:
                if (raw is DateTime dt)
                {
                    var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                    cell.Value = utc;
                    cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                    return;
                }
                break;

            case GridColumnDataType.Boolean:
                if (raw is bool b)
                {
                    cell.Value = CsvGridExportRenderer.LocaliseBool(b, language);
                    return;
                }
                break;

            case GridColumnDataType.Integer:
                switch (raw)
                {
                    case int i: cell.Value = i; return;
                    case long l: cell.Value = l; return;
                    case short s: cell.Value = s; return;
                    case byte by: cell.Value = by; return;
                }
                break;

            case GridColumnDataType.Decimal:
                switch (raw)
                {
                    case decimal dec: cell.Value = dec; return;
                    case double dbl: cell.Value = dbl; return;
                    case float f: cell.Value = (decimal)f; return;
                }
                break;

            case GridColumnDataType.Text:
            default:
                break;
        }

        // Fallback — invariant-culture string projection.
        cell.Value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Excel limits sheet names to 31 characters and disallows several
    /// characters (<c>\ / ? * [ ]</c>). Truncate + sanitise so caller-supplied
    /// titles never produce a SaveAs exception.
    /// </summary>
    /// <param name="name">Caller-supplied sheet title.</param>
    /// <returns>An Excel-legal sheet name.</returns>
    internal static string TruncateSheetName(string name)
    {
        var sanitised = new string((name ?? "Sheet1")
            .Select(c => c is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : c).ToArray());
        if (sanitised.Length > 31)
        {
            sanitised = sanitised[..31];
        }
        if (string.IsNullOrWhiteSpace(sanitised))
        {
            sanitised = "Sheet1";
        }
        return sanitised;
    }

    /// <summary>
    /// Builds the suggested filename: <c>{GridName}-yyyyMMdd-HHmm.xlsx</c>.
    /// </summary>
    /// <param name="gridName">Grid identifier from the request.</param>
    /// <param name="nowUtc">Clock-supplied current UTC instant.</param>
    /// <returns>Suggested filename.</returns>
    internal static string SuggestFileName(string gridName, DateTime nowUtc)
    {
        var stamp = nowUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"{gridName}-{stamp}.xlsx";
    }
}
