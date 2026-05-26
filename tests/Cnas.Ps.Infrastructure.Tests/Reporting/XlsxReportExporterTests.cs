using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using Cnas.Ps.Contracts;
using Cnas.Ps.Infrastructure.Services.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — happy-path tests for <see cref="XlsxReportExporter"/>.
/// Round-trips the rendered XLSX bytes through ClosedXML to assert the
/// worksheet carries the header row and the data rows in the expected
/// position.
/// </summary>
public sealed class XlsxReportExporterTests
{
    /// <summary>Baseline columns shared across XLSX tests.</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Timestamp"),
        new("Actor"),
        new("Action"),
    ];

    /// <summary>Baseline rows shared across XLSX tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["2026-05-24T10:00:00Z", "alice", "LOGIN"],
        ["2026-05-24T10:01:00Z", "bob",   "LOGOUT"],
    ];

    /// <summary>Happy path — bytes round-trip through ClosedXML; headers + data are preserved.</summary>
    [Fact]
    public async Task ExportAsync_HappyPath_OpensInClosedXmlAndPreservesMatrix()
    {
        var sut = new XlsxReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit log report",
            Columns: BaselineColumns,
            Rows: BaselineRows);

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        result.Value.FileExtension.Should().Be(".xlsx");
        result.Value.Format.Should().Be(ReportExportFormat.Xlsx);

        // OOXML files start with the 'PK' zip signature.
        result.Value.Bytes.Should().StartWith(new byte[] { 0x50, 0x4B });

        using var ms = new MemoryStream(result.Value.Bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();

        // Sheet name is derived from the report title (truncated to 31 chars).
        ws.Name.Should().StartWith("Audit log report");

        // Row 1 carries the headers.
        ws.Cell(1, 1).GetString().Should().Be("Timestamp");
        ws.Cell(1, 2).GetString().Should().Be("Actor");
        ws.Cell(1, 3).GetString().Should().Be("Action");

        // Row 2 + 3 carry the data.
        ws.Cell(2, 1).GetString().Should().Be("2026-05-24T10:00:00Z");
        ws.Cell(2, 2).GetString().Should().Be("alice");
        ws.Cell(3, 2).GetString().Should().Be("bob");
    }

    /// <summary>Empty rows still produce a valid workbook with the header row.</summary>
    [Fact]
    public async Task ExportAsync_NoRows_StillProducesValidWorkbook()
    {
        var sut = new XlsxReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit empty",
            Columns: BaselineColumns,
            Rows: System.Array.Empty<IReadOnlyList<string>>());

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var ms = new MemoryStream(result.Value.Bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();
        ws.Cell(1, 1).GetString().Should().Be("Timestamp");
    }
}
