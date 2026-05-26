using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cnas.Ps.Contracts;
using Cnas.Ps.Infrastructure.Services.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — happy-path tests for <see cref="CsvReportExporter"/>.
/// Verifies the canonical MIME, the UTF-8 BOM, and that the rendered text
/// embeds the headers + the row payload.
/// </summary>
public sealed class CsvReportExporterTests
{
    /// <summary>Baseline columns shared across CSV tests.</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Timestamp"),
        new("Actor"),
        new("Action"),
    ];

    /// <summary>Baseline rows shared across CSV tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["2026-05-24T10:00:00Z", "alice", "LOGIN"],
        ["2026-05-24T10:01:00Z", "bob,with,commas", "LOGOUT"],
    ];

    /// <summary>Happy path — bytes are well-formed CSV with the UTF-8 BOM.</summary>
    [Fact]
    public async Task ExportAsync_HappyPath_ReturnsCsvWithBomAndEmbeddedRows()
    {
        var sut = new CsvReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit",
            Columns: BaselineColumns,
            Rows: BaselineRows);

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be("text/csv; charset=utf-8");
        result.Value.FileExtension.Should().Be(".csv");
        result.Value.Format.Should().Be(ReportExportFormat.Csv);

        // First three bytes are the UTF-8 BOM (EF BB BF).
        result.Value.Bytes.Should().StartWith(new byte[] { 0xEF, 0xBB, 0xBF });

        // Body should contain the header row and the comma-bearing actor field
        // properly escaped with quotes.
        var text = Encoding.UTF8.GetString(result.Value.Bytes);
        text.Should().Contain("Timestamp,Actor,Action");
        text.Should().Contain("alice");
        text.Should().Contain("\"bob,with,commas\"");
    }

    /// <summary>Empty rows produce a header-only CSV (validator allows zero rows once the schema is valid).</summary>
    [Fact]
    public async Task ExportAsync_NoRows_ReturnsHeaderOnly()
    {
        var sut = new CsvReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit",
            Columns: BaselineColumns,
            Rows: System.Array.Empty<IReadOnlyList<string>>());

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var text = Encoding.UTF8.GetString(result.Value.Bytes);
        text.TrimEnd('\r', '\n').Should().EndWith("Action");
    }
}
