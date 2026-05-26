using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cnas.Ps.Contracts;
using Cnas.Ps.Infrastructure.Services.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — happy-path tests for <see cref="PdfReportExporter"/>.
/// Pins the PDF magic-byte contract (<c>%PDF-</c> header) — the exporter
/// must always emit a syntactically-valid PDF even on dev runtimes where
/// QuestPDF's native SkiaSharp binaries cannot load (the minimal hand-rolled
/// fallback satisfies the same contract).
/// </summary>
public sealed class PdfReportExporterTests
{
    /// <summary>Baseline columns shared across PDF tests.</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Timestamp"),
        new("Actor"),
        new("Action"),
    ];

    /// <summary>Baseline rows shared across PDF tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["2026-05-24T10:00:00Z", "alice", "LOGIN"],
        ["2026-05-24T10:01:00Z", "bob",   "LOGOUT"],
    ];

    /// <summary>Bytes start with the PDF magic header.</summary>
    [Fact]
    public async Task ExportAsync_HappyPath_StartsWithPdfMagicBytes()
    {
        var sut = new PdfReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Audit log report",
            Columns: BaselineColumns,
            Rows: BaselineRows);

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be("application/pdf");
        result.Value.FileExtension.Should().Be(".pdf");
        result.Value.Format.Should().Be(ReportExportFormat.Pdf);

        // PDF magic: bytes 0..4 = "%PDF-".
        var magic = Encoding.ASCII.GetString(result.Value.Bytes, 0, 5);
        magic.Should().Be("%PDF-");

        // Body must be non-trivial — the minimal fallback alone is >200 bytes,
        // and any QuestPDF rendering is much larger.
        result.Value.Bytes.Length.Should().BeGreaterThan(200);
    }

    /// <summary>Empty rows still produce a valid PDF.</summary>
    [Fact]
    public async Task ExportAsync_NoRows_StillProducesValidPdf()
    {
        var sut = new PdfReportExporter();
        var input = new ReportExportInputDto(
            ReportTitle: "Empty",
            Columns: BaselineColumns,
            Rows: System.Array.Empty<IReadOnlyList<string>>());

        var result = await sut.ExportAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var magic = Encoding.ASCII.GetString(result.Value.Bytes, 0, 5);
        magic.Should().Be("%PDF-");
    }
}
