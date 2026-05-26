using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPdfDocument = QuestPDF.Fluent.Document;

namespace Cnas.Ps.Infrastructure.Exports;

/// <summary>
/// R0226 / TOR UI 013 — PDF implementation of <see cref="IGridExportRenderer"/>
/// backed by <c>QuestPDF</c> (already pinned in
/// <c>Directory.Packages.props</c>, no new NuGet dependency). Falls back to a
/// minimal hand-rolled PDF on dev runtimes where QuestPDF's native SkiaSharp
/// binaries cannot load (e.g. <c>win-arm64</c>) — mirrors the established
/// pattern in <c>ReportingService.RenderPdf</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless + thread-safe.</b> Per-call <see cref="QuestPdfDocument"/>
/// instances mean concurrent renders never share document state. The one-time
/// licence probe is gated by a process-static <see cref="Lock"/>.
/// </para>
/// </remarks>
public sealed class PdfGridExportRenderer : IGridExportRenderer
{
    /// <summary>MIME type for the PDF wire format.</summary>
    internal const string PdfMimeType = "application/pdf";

    /// <summary>
    /// Cached result of the one-shot QuestPDF probe. <c>null</c> until the
    /// first render attempt; <c>true</c> once the licence has been set
    /// successfully; <c>false</c> if QuestPDF's native deps refused to load.
    /// Mirrors <see cref="Cnas.Ps.Infrastructure.Services.ReportingService"/>.
    /// </summary>
    private static bool? s_questPdfAvailable;

    /// <summary>Synchronises the one-shot QuestPDF probe across concurrent first calls.</summary>
    private static readonly Lock s_questPdfLock = new();

    /// <summary>Clock abstraction used to stamp the suggested filename + the PDF header.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>Builds a PDF renderer with the supplied clock.</summary>
    /// <param name="clock">Clock used to stamp the document header + filename.</param>
    public PdfGridExportRenderer(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Parameterless convenience constructor that wires
    /// <see cref="SystemTimeProvider"/> — used by unit tests that don't care
    /// about the exact timestamp value.
    /// </summary>
    public PdfGridExportRenderer() : this(new SystemTimeProvider()) { }

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Pdf;

    /// <inheritdoc />
    public Task<Result<GridExportResult>> RenderAsync(
        GridExportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var generatedAt = _clock.UtcNow.ToString("u", CultureInfo.InvariantCulture);
        byte[] payload;
        if (TryInitializeQuestPdf())
        {
            try
            {
                payload = RenderWithQuestPdf(request, generatedAt);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Mirror ReportingService — fall back to the minimal emitter
                // when QuestPDF's probe passed but rendering failed for some
                // other runtime reason (font missing, page-break edge case).
                payload = RenderMinimalPdf(request, generatedAt);
            }
        }
        else
        {
            payload = RenderMinimalPdf(request, generatedAt);
        }

        var fileName = SuggestFileName(request.GridName, _clock.UtcNow);
        var resultValue = new GridExportResult(
            Content: payload,
            ContentType: PdfMimeType,
            SuggestedFileName: fileName);
        return Task.FromResult(Result<GridExportResult>.Success(resultValue));
    }

    /// <summary>
    /// Renders <paramref name="request"/> as a QuestPDF document and returns
    /// the bytes.
    /// </summary>
    /// <param name="request">Grid request.</param>
    /// <param name="generatedAt">Already-formatted "u" timestamp.</param>
    /// <returns>The PDF bytes.</returns>
    private static byte[] RenderWithQuestPdf(GridExportRequest request, string generatedAt)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? request.GridName : request.Title!;
        var document = QuestPdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(25);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).SemiBold().FontSize(14);
                    col.Item().Text($"Generated (UTC): {generatedAt}").FontSize(9);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        for (var i = 0; i < request.Columns.Count; i++)
                        {
                            cols.RelativeColumn();
                        }
                    });

                    table.Header(header =>
                    {
                        foreach (var column in request.Columns)
                        {
                            header.Cell().BorderBottom(1).PaddingVertical(2).Text(column.Header).SemiBold();
                        }
                    });

                    foreach (var row in request.Rows)
                    {
                        foreach (var column in request.Columns)
                        {
                            row.Cells.TryGetValue(column.FieldName, out var raw);
                            var text = CsvGridExportRenderer.FormatCell(raw, column.DataType, request.Language);
                            table.Cell().PaddingVertical(1).Text(text);
                        }
                    }
                });

                page.Footer().AlignRight().Text(text =>
                {
                    if (!string.IsNullOrWhiteSpace(request.FooterNote))
                    {
                        text.Span($"{request.FooterNote} — ");
                    }
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
    /// Hand-rolled minimal PDF 1.4 fallback. Produces a syntactically-valid
    /// document with a single page that lists the title, timestamp, and rows
    /// as plain Helvetica text. Satisfies the PDF magic-byte contract on dev
    /// runtimes (e.g. <c>win-arm64</c>) that lack SkiaSharp natives. Adapted
    /// from <c>ReportingService.RenderMinimalPdf</c>.
    /// </summary>
    /// <param name="request">Grid request.</param>
    /// <param name="generatedAt">Already-formatted "u" timestamp.</param>
    /// <returns>The PDF bytes.</returns>
    private static byte[] RenderMinimalPdf(GridExportRequest request, string generatedAt)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? request.GridName : request.Title!;
        var lines = new List<string>(capacity: request.Rows.Count + 4)
        {
            title,
            $"Generated (UTC): {generatedAt}",
            string.Empty,
            string.Join(" | ", request.Columns.Select(c => c.Header)),
        };
        foreach (var row in request.Rows)
        {
            var cells = request.Columns.Select(c =>
            {
                row.Cells.TryGetValue(c.FieldName, out var raw);
                return CsvGridExportRenderer.FormatCell(raw, c.DataType, request.Language);
            });
            lines.Add(string.Join(" | ", cells));
        }

        // PDF content stream — one Tj per line, top-down on a single page.
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
        var offsets = new List<long>();

        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        // Binary marker (advisory) so consumers don't mis-detect as text.
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
    /// Escapes a string for inclusion inside a PDF literal string. Backslash,
    /// open paren, and close paren are escaped per PDF reference 7.3.4.2.
    /// Non-ASCII characters are stripped (the minimal fallback writes
    /// Helvetica without an embedded encoding so non-ASCII would render as
    /// blanks anyway).
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

    /// <summary>
    /// Builds the suggested filename: <c>{GridName}-yyyyMMdd-HHmm.pdf</c>.
    /// </summary>
    /// <param name="gridName">Grid identifier from the request.</param>
    /// <param name="nowUtc">Clock-supplied current UTC instant.</param>
    /// <returns>Suggested filename.</returns>
    internal static string SuggestFileName(string gridName, DateTime nowUtc)
    {
        var stamp = nowUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"{gridName}-{stamp}.pdf";
    }
}
