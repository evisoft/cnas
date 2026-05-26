using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cnas.Ps.Contracts;
using Cnas.Ps.Infrastructure.Services.Reporting;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — happy-path tests for <see cref="DocxReportExporter"/>.
/// Round-trips the rendered DOCX bytes through the OpenXML SDK to assert
/// the document carries the title paragraph + the table layout. The
/// <c>PK</c> zip-magic check pins the wire shape so trimmed deployments
/// cannot ship a placeholder that still claims success.
/// </summary>
public sealed class DocxReportExporterTests
{
    /// <summary>Baseline columns shared across DOCX tests.</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Timestamp"),
        new("Actor"),
        new("Action"),
    ];

    /// <summary>Baseline rows shared across DOCX tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["2026-05-24T10:00:00Z", "alice", "LOGIN"],
        ["2026-05-24T10:01:00Z", "bob",   "LOGOUT"],
    ];

    /// <summary>Bytes start with the OOXML zip-magic and round-trip through the OpenXML SDK.</summary>
    [Fact]
    public async Task ExportAsync_HappyPath_ProducesOpenXmlDocumentWithTableRows()
    {
        var sut = new DocxReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit log report",
            Columns: BaselineColumns,
            Rows: BaselineRows);

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        result.Value.FileExtension.Should().Be(".docx");
        result.Value.Format.Should().Be(ReportExportFormat.Docx);

        // OOXML files start with the 'PK' zip signature.
        result.Value.Bytes.Should().StartWith(new byte[] { 0x50, 0x4B });

        // Round-trip — open the document and verify the table layout.
        using var ms = new MemoryStream(result.Value.Bytes);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var body = doc.MainDocumentPart!.Document!.Body!;

        // First paragraph carries the report title.
        var firstParagraphText = body.Descendants<Paragraph>().First().InnerText;
        firstParagraphText.Should().Be("Audit log report");

        // The table has 1 header row + 2 data rows = 3 rows total.
        var tableRows = body.Descendants<TableRow>().ToList();
        tableRows.Should().HaveCount(3);

        // Header row cells.
        var headerCells = tableRows[0].Descendants<TableCell>().Select(c => c.InnerText).ToList();
        headerCells.Should().BeEquivalentTo("Timestamp", "Actor", "Action");

        // First data row cells.
        var firstDataCells = tableRows[1].Descendants<TableCell>().Select(c => c.InnerText).ToList();
        firstDataCells.Should().BeEquivalentTo("2026-05-24T10:00:00Z", "alice", "LOGIN");
    }

    /// <summary>Empty rows still produce a valid document carrying only the header row.</summary>
    [Fact]
    public async Task ExportAsync_NoRows_StillProducesValidDocumentWithHeaderOnly()
    {
        var sut = new DocxReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Empty",
            Columns: BaselineColumns,
            Rows: System.Array.Empty<IReadOnlyList<string>>());

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var ms = new MemoryStream(result.Value.Bytes);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var tableRows = doc.MainDocumentPart!.Document!.Body!.Descendants<TableRow>().ToList();
        tableRows.Should().HaveCount(1); // header row only
    }
}
