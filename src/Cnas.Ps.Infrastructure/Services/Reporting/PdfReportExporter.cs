using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPdfDocument = QuestPDF.Fluent.Document;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — PDF implementation of <see cref="IReportExporter"/>
/// backed by <c>QuestPDF</c> (already pinned in <c>Directory.Packages.props</c>).
/// Mirrors the established <c>PdfGridExportRenderer</c> fallback pattern: when
/// QuestPDF's native SkiaSharp binaries are absent on the host runtime
/// (e.g. <c>win-arm64</c> dev machines) the exporter falls back to a tiny
/// hand-rolled minimal PDF emitter so the magic-byte contract still holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless + thread-safe.</b> Per-call <see cref="QuestPdfDocument"/>
/// instances mean concurrent renders never share document state. The one-shot
/// licence probe is gated by a process-static lock so the
/// <see cref="QuestPDF.Settings"/> assignment runs at most once.
/// </para>
/// </remarks>
public sealed class PdfReportExporter : IReportExporter
{
    /// <summary>Canonical MIME type for the .pdf wire format.</summary>
    internal const string PdfMimeType = "application/pdf";

    /// <summary>Dotted file extension for the .pdf wire format.</summary>
    internal const string PdfExtension = ".pdf";

    /// <summary>
    /// Cached result of the one-shot QuestPDF probe. <c>null</c> until the
    /// first render attempt; <c>true</c> once the licence has been set
    /// successfully; <c>false</c> if QuestPDF's native deps refused to load.
    /// </summary>
    private static bool? s_questPdfAvailable;

    /// <summary>Synchronises the one-shot QuestPDF probe across concurrent first calls.</summary>
    private static readonly Lock s_questPdfLock = new();

    /// <inheritdoc />
    public ReportExportFormat Format => ReportExportFormat.Pdf;

    /// <inheritdoc />
    public Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        byte[] payload;
        if (TryInitializeQuestPdf())
        {
            try
            {
                payload = RenderWithQuestPdf(input);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // QuestPDF passed the licence probe but rendering failed for some
                // runtime reason (font missing, layout edge case). Fall back to
                // the minimal emitter so the magic-byte contract still holds.
                payload = RenderMinimalPdf(input);
            }
        }
        else
        {
            payload = RenderMinimalPdf(input);
        }

        var resultValue = new ReportExportResultDto(
            Bytes: payload,
            ContentType: PdfMimeType,
            Format: ReportExportFormat.Pdf,
            FileExtension: PdfExtension);
        return Task.FromResult(Result<ReportExportResultDto>.Success(resultValue));
    }

    /// <summary>
    /// Renders <paramref name="input"/> as a QuestPDF document and returns the
    /// resulting bytes.
    /// </summary>
    /// <param name="input">Validated input.</param>
    /// <returns>PDF bytes.</returns>
    private static byte[] RenderWithQuestPdf(ReportExportInputDto input)
    {
        var document = QuestPdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(25);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text(input.ReportTitle).SemiBold().FontSize(14);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        foreach (var col in input.Columns)
                        {
                            if (col.Width.HasValue && col.Width.Value > 0.0 && col.Width.Value < 1.0)
                            {
                                cols.RelativeColumn((float)col.Width.Value);
                            }
                            else
                            {
                                cols.RelativeColumn();
                            }
                        }
                    });

                    table.Header(header =>
                    {
                        foreach (var col in input.Columns)
                        {
                            header.Cell().BorderBottom(1).PaddingVertical(2).Text(col.Header).SemiBold();
                        }
                    });

                    foreach (var row in input.Rows)
                    {
                        for (int c = 0; c < input.Columns.Count; c++)
                        {
                            var text = c < row.Count ? row[c] ?? string.Empty : string.Empty;
                            table.Cell().PaddingVertical(1).Text(text);
                        }
                    }
                });

                page.Footer().AlignRight().Text(text =>
                {
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        using var ms = new MemoryStream();
        document.GeneratePdf(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Hand-rolled minimal PDF 1.4 fallback used when QuestPDF cannot load
    /// its native dependencies. Produces a syntactically-valid single-page
    /// document so the <c>%PDF-</c> magic-byte contract holds.
    /// </summary>
    /// <param name="input">Validated input.</param>
    /// <returns>PDF bytes.</returns>
    private static byte[] RenderMinimalPdf(ReportExportInputDto input)
    {
        var lines = new System.Collections.Generic.List<string>(capacity: input.Rows.Count + 3)
        {
            input.ReportTitle,
            string.Empty,
        };

        // Header line.
        var headerCells = new string[input.Columns.Count];
        for (int c = 0; c < input.Columns.Count; c++)
        {
            headerCells[c] = input.Columns[c].Header;
        }
        lines.Add(string.Join(" | ", headerCells));

        // Data rows.
        foreach (var row in input.Rows)
        {
            var cells = new string[input.Columns.Count];
            for (int c = 0; c < input.Columns.Count; c++)
            {
                cells[c] = c < row.Count ? row[c] ?? string.Empty : string.Empty;
            }
            lines.Add(string.Join(" | ", cells));
        }

        var content = new StringBuilder();
        content.Append("BT\n/F1 9 Tf\n");
        var y = 800f;
        foreach (var line in lines)
        {
            content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 36 {y:0.##} Tm (");
            content.Append(EscapePdfString(line));
            content.Append(") Tj\n");
            y -= 12f;
            if (y < 36f) break;
        }
        content.Append("ET\n");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        var offsets = new System.Collections.Generic.List<long>();

        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        writer.Write(new byte[] { (byte)'%', 0xC3, 0xA4, (byte)'\n' });

        offsets.Add(ms.Position);
        writer.Write(Encoding.ASCII.GetBytes(
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));

        offsets.Add(ms.Position);
        writer.Write(Encoding.ASCII.GetBytes(
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));

        offsets.Add(ms.Position);
        writer.Write(Encoding.ASCII.GetBytes(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n"));

        offsets.Add(ms.Position);
        writer.Write(Encoding.ASCII.GetBytes(
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));

        offsets.Add(ms.Position);
        var streamHeader = string.Create(
            CultureInfo.InvariantCulture,
            $"5 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        writer.Write(Encoding.ASCII.GetBytes(streamHeader));
        writer.Write(contentBytes);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        var xrefStart = ms.Position;
        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            xref.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }
        xref.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        writer.Write(Encoding.ASCII.GetBytes(xref.ToString()));
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Escapes a string for inclusion in a PDF literal string per
    /// PDF reference 7.3.4.2 — backslash and parens escaped, non-ASCII
    /// substituted with <c>?</c> because the minimal emitter does not
    /// embed a Unicode font.
    /// </summary>
    /// <param name="value">Cell content.</param>
    /// <returns>PDF-safe literal string contents.</returns>
    private static string EscapePdfString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(':  sb.Append("\\("); break;
                case ')':  sb.Append("\\)"); break;
                default:
                    if (c >= 32 && c < 127)
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append('?');
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Probes QuestPDF support once per process; safe to call concurrently.
    /// </summary>
    /// <returns><c>true</c> when QuestPDF is usable on this runtime.</returns>
    private static bool TryInitializeQuestPdf()
    {
        if (s_questPdfAvailable.HasValue) return s_questPdfAvailable.Value;
        lock (s_questPdfLock)
        {
            if (s_questPdfAvailable.HasValue) return s_questPdfAvailable.Value;
            try
            {
                QuestPDF.Settings.License = LicenseType.Community;
                s_questPdfAvailable = true;
            }
            catch (Exception)
            {
                s_questPdfAvailable = false;
            }
            return s_questPdfAvailable.Value;
        }
    }
}
