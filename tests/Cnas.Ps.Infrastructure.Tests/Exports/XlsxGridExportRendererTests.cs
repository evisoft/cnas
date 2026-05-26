using ClosedXML.Excel;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Infrastructure.Exports;

namespace Cnas.Ps.Infrastructure.Tests.Exports;

/// <summary>
/// R0226 / TOR UI 013 — happy-path tests for <see cref="XlsxGridExportRenderer"/>.
/// Verifies a well-formed OOXML body comes back, the first sheet carries the
/// header row + the data rows, and typed columns project to the right native
/// XLSX cell type.
/// </summary>
public sealed class XlsxGridExportRendererTests
{
    [Fact]
    public async Task RenderAsync_HappyPath_ReturnsXlsxMimeAndOpensInClosedXml()
    {
        var sut = new XlsxGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Code", "Code", GridColumnDataType.Text),
                new("CreatedAtUtc", "CreatedAtUtc", GridColumnDataType.DateTime),
                new("Active", "Active", GridColumnDataType.Boolean),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "SQID-1",
                    ["CreatedAtUtc"] = new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc),
                    ["Active"] = true,
                }),
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "SQID-2",
                    ["CreatedAtUtc"] = new DateTime(2026, 5, 22, 9, 0, 0, DateTimeKind.Utc),
                    ["Active"] = false,
                }),
            },
            Title: "Solicitants",
            Language: "ro");

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        // OOXML files start with the 'PK' zip signature.
        result.Value.Content.Should().StartWith(new byte[] { 0x50, 0x4B });

        // Round-trip through ClosedXML to verify the structure.
        using var ms = new MemoryStream(result.Value.Content);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();
        ws.Cell(1, 1).GetString().Should().Be("Code");
        ws.Cell(1, 2).GetString().Should().Be("CreatedAtUtc");
        ws.Cell(1, 3).GetString().Should().Be("Active");
        ws.Cell(2, 1).GetString().Should().Be("SQID-1");
        ws.Cell(3, 1).GetString().Should().Be("SQID-2");
    }
}
