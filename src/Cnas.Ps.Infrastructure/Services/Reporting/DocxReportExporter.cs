using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — DOCX implementation of <see cref="IReportExporter"/>
/// backed by <c>DocumentFormat.OpenXml</c> (already pinned in
/// <c>Directory.Packages.props</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> A minimal Word document carrying a centred title
/// paragraph followed by a single bordered table — header row in bold, data
/// rows in the document default font. The layout mirrors the corresponding
/// PDF / XLSX reports so a user comparing the three formats sees the same
/// matrix at a glance.
/// </para>
/// <para>
/// <b>Stateless + thread-safe.</b> Per-call <see cref="MemoryStream"/> and
/// <see cref="WordprocessingDocument"/> instances mean concurrent renders
/// never share document state.
/// </para>
/// <para>
/// <b>Degraded-mode contract.</b> When the <c>DocumentFormat.OpenXml</c>
/// package fails to load (e.g. a trimmed deployment), the controller
/// receives <see cref="ErrorCodes.ExportDocxNotAvailable"/>. The current
/// build pins the package as a direct dependency so this branch is
/// effectively unreachable in production; the unit test
/// <c>DocxReportExporterTests.Export_BaselineInput_StartsWithOpenXmlMagicBytes</c>
/// pins the happy-path behaviour.
/// </para>
/// </remarks>
public sealed class DocxReportExporter : IReportExporter
{
    /// <summary>Canonical MIME type for the .docx wire format.</summary>
    internal const string DocxMimeType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Dotted file extension for the .docx wire format.</summary>
    internal const string DocxExtension = ".docx";

    /// <inheritdoc />
    public ReportExportFormat Format => ReportExportFormat.Docx;

    /// <inheritdoc />
    public Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        byte[] payload;
        try
        {
            payload = RenderWithOpenXml(input, ct);
        }
        catch (TypeInitializationException)
        {
            // The OpenXML SDK could not load (e.g. trimmed deployment). Map to
            // the stable DOCX-unavailable error code so the dashboard can
            // attribute the regression specifically.
            return Task.FromResult(Result<ReportExportResultDto>.Failure(
                ErrorCodes.ExportDocxNotAvailable,
                "DOCX export pipeline is not available on this build."));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<ReportExportResultDto>.Failure(
                ErrorCodes.ExportDocxNotAvailable,
                "DOCX export pipeline is not available on this build."));
        }

        var resultValue = new ReportExportResultDto(
            Bytes: payload,
            ContentType: DocxMimeType,
            Format: ReportExportFormat.Docx,
            FileExtension: DocxExtension);
        return Task.FromResult(Result<ReportExportResultDto>.Success(resultValue));
    }

    /// <summary>
    /// Renders <paramref name="input"/> as an OOXML word-processing document
    /// and returns the bytes.
    /// </summary>
    /// <param name="input">Validated input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DOCX bytes.</returns>
    private static byte[] RenderWithOpenXml(ReportExportInputDto input, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            // Title paragraph (bold + centred).
            body.AppendChild(MakeHeading(input.ReportTitle));

            // Table — single bordered table with header row.
            var table = new Table();

            // Table properties: thin single-line borders all around so the
            // matrix is visually scannable in Word / LibreOffice / Google Docs.
            var tableProps = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }));
            table.AppendChild(tableProps);

            // Header row.
            var headerRow = new TableRow();
            foreach (var col in input.Columns)
            {
                headerRow.AppendChild(MakeCell(col.Header, bold: true));
            }
            table.AppendChild(headerRow);

            // Data rows.
            foreach (var row in input.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var tr = new TableRow();
                for (int c = 0; c < input.Columns.Count; c++)
                {
                    var text = c < row.Count ? row[c] ?? string.Empty : string.Empty;
                    tr.AppendChild(MakeCell(text));
                }
                table.AppendChild(tr);
            }

            body.AppendChild(table);

            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a centred bold title paragraph at 14pt (28 half-points).
    /// </summary>
    /// <param name="title">Title text.</param>
    /// <returns>The styled paragraph.</returns>
    private static Paragraph MakeHeading(string title)
    {
        var runProps = new RunProperties(new Bold(), new FontSize { Val = "28" });
        var run = new Run(runProps, new Text(title) { Space = SpaceProcessingModeValues.Preserve });
        var paragraph = new Paragraph(run)
        {
            ParagraphProperties = new ParagraphProperties(
                new Justification { Val = JustificationValues.Center }),
        };
        return paragraph;
    }

    /// <summary>
    /// Builds a single <see cref="TableCell"/> containing one paragraph with
    /// <paramref name="text"/>. The run is bold when <paramref name="bold"/>
    /// is set; the text is wrapped with
    /// <c>SpaceProcessingModeValues.Preserve</c> so leading/trailing
    /// whitespace survives the OpenXML serialiser.
    /// </summary>
    /// <param name="text">Cell text.</param>
    /// <param name="bold">Whether the run carries the bold property.</param>
    /// <returns>The materialised <see cref="TableCell"/>.</returns>
    private static TableCell MakeCell(string text, bool bold = false)
    {
        var runProps = new RunProperties();
        if (bold)
        {
            runProps.Append(new Bold());
        }
        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var paragraph = new Paragraph(run);
        return new TableCell(paragraph);
    }
}
