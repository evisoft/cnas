using System.Text;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Infrastructure.Exports;

namespace Cnas.Ps.Infrastructure.Tests.Exports;

/// <summary>
/// R0226 / TOR UI 013 — unit tests for <see cref="CsvGridExportRenderer"/>. Covers
/// happy-path CSV emission, RFC 4180 quoting of pathological cell values, locale-
/// dependent boolean formatting, and date/datetime ISO formatting.
/// </summary>
public sealed class CsvGridExportRendererTests
{
    /// <summary>UTF-8 byte-order mark — the first three bytes of every CSV body.</summary>
    private static readonly byte[] Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>Builds a small request with three text columns and three data rows.</summary>
    private static GridExportRequest SmallTextRequest() =>
        new(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Code", "Code", GridColumnDataType.Text),
                new("Name", "Name", GridColumnDataType.Text),
                new("Status", "Status", GridColumnDataType.Text),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "A1",
                    ["Name"] = "Ion",
                    ["Status"] = "Active",
                }),
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "B2",
                    ["Name"] = "Maria",
                    ["Status"] = "Active",
                }),
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "C3",
                    ["Name"] = "Petru",
                    ["Status"] = "Inactive",
                }),
            },
            Language: "ro");

    /// <summary>
    /// Strips the UTF-8 BOM and decodes the body to a string for assertion convenience.
    /// </summary>
    /// <param name="bytes">Renderer output.</param>
    /// <returns>The CSV body as a string (no BOM).</returns>
    private static string Decode(byte[] bytes)
    {
        bytes.Should().NotBeNull();
        bytes.Length.Should().BeGreaterThan(3);
        bytes.Take(3).Should().BeEquivalentTo(Bom, "the renderer must emit a UTF-8 BOM so Excel detects the encoding.");
        return Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);
    }

    [Fact]
    public async Task RenderAsync_HappyPath_EmitsHeaderAndDataRows_WithBom()
    {
        var sut = new CsvGridExportRenderer();

        var result = await sut.RenderAsync(SmallTextRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().StartWith("text/csv");
        var body = Decode(result.Value.Content);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4, "1 header row + 3 data rows.");
        lines[0].TrimEnd('\r').Should().Be("Code,Name,Status");
        lines[1].TrimEnd('\r').Should().Be("A1,Ion,Active");
        lines[2].TrimEnd('\r').Should().Be("B2,Maria,Active");
        lines[3].TrimEnd('\r').Should().Be("C3,Petru,Inactive");
    }

    [Fact]
    public async Task RenderAsync_CellWithComma_QuoteAndNewline_QuotesPerRfc4180()
    {
        // The cell contains every quote-triggering character of RFC 4180.
        var sut = new CsvGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Code", "Code", GridColumnDataType.Text),
                new("Description", "Description", GridColumnDataType.Text),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?>
                {
                    ["Code"] = "X",
                    // Comma, embedded double-quote, embedded newline → must quote AND escape "
                    ["Description"] = "Hello, \"world\"\nNext line",
                }),
            });

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        var body = Decode(result.Value.Content);
        // The renderer must wrap the cell in double-quotes and double the inner ".
        body.Should().Contain("\"Hello, \"\"world\"\"\nNext line\"");
    }

    [Fact]
    public async Task RenderAsync_BoolCells_LocaleRo_EmitsDaNu()
    {
        var sut = new CsvGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Active?", "Active", GridColumnDataType.Boolean),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?> { ["Active"] = true }),
                new(new Dictionary<string, object?> { ["Active"] = false }),
            },
            Language: "ro");

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        var body = Decode(result.Value.Content);
        body.Should().Contain("Da");
        body.Should().Contain("Nu");
    }

    [Theory]
    [InlineData("en", "Yes", "No")]
    [InlineData("ru", "Да", "Нет")]
    public async Task RenderAsync_BoolCells_LocalisedYesNo(string lang, string yes, string no)
    {
        var sut = new CsvGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Active?", "Active", GridColumnDataType.Boolean),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?> { ["Active"] = true }),
                new(new Dictionary<string, object?> { ["Active"] = false }),
            },
            Language: lang);

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        var body = Decode(result.Value.Content);
        body.Should().Contain(yes);
        body.Should().Contain(no);
    }

    [Fact]
    public async Task RenderAsync_DateCells_EmitsIsoYearMonthDay()
    {
        var sut = new CsvGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("D", "D", GridColumnDataType.Date),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?>
                {
                    ["D"] = new DateTime(2026, 5, 21, 10, 30, 0, DateTimeKind.Utc),
                }),
            });

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        var body = Decode(result.Value.Content);
        body.Should().Contain("2026-05-21");
        body.Should().NotContain("10:30");
    }

    [Fact]
    public async Task RenderAsync_DateTimeCells_EmitsIsoUtcZ()
    {
        var sut = new CsvGridExportRenderer();
        var request = new GridExportRequest(
            GridName: "Solicitants",
            Columns: new GridColumn[]
            {
                new("Dt", "Dt", GridColumnDataType.DateTime),
            },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?>
                {
                    ["Dt"] = new DateTime(2026, 5, 21, 10, 30, 5, DateTimeKind.Utc),
                }),
            });

        var result = await sut.RenderAsync(request);

        result.IsSuccess.Should().BeTrue();
        var body = Decode(result.Value.Content);
        body.Should().Contain("2026-05-21T10:30:05Z");
    }
}
