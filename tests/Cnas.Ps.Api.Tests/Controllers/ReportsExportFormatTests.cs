using System.Text;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0154 / TOR CF 03.14, CF 09.07, UI 013 — controller-level integration tests
/// that pin the <c>GET /api/reports/{code}/export?format=…</c> endpoint to
/// emit the canonical Content-Type for each of the four supported export
/// formats (CSV / XLSX / DOCX / PDF). Iter 113 shipped the underlying
/// <see cref="IReportExportSelector"/> + 4 <see cref="IReportExporter"/>
/// implementations; iter 118 adds these tests to fence the controller's
/// dispatch behaviour against regressions when new formats are added.
/// </summary>
/// <remarks>
/// <para>
/// All tests follow the same shape:
/// <list type="number">
///   <item>Stub the <see cref="IReportingService"/> CSV generator to return a tiny matrix.</item>
///   <item>Stub the <see cref="IReportExportSelector"/> to surface the canonical envelope per format.</item>
///   <item>Assert the controller returns a <see cref="FileContentResult"/> with the right MIME and filename extension.</item>
/// </list>
/// </para>
/// <para>
/// These are unit tests with direct controller construction (same pattern as
/// <see cref="ReportsControllerTests"/>), not WebApplicationFactory boots —
/// the goal is to fence the controller-to-selector wiring, not the HTTP
/// pipeline (route/auth coverage already lives in the auth-policy tests).
/// </para>
/// </remarks>
public sealed class ReportsExportFormatTests
{
    /// <summary>Canonical MIME emitted by <c>CsvReportExporter</c>.</summary>
    private const string CsvMimeType = "text/csv; charset=utf-8";

    /// <summary>Canonical MIME emitted by <c>XlsxReportExporter</c>.</summary>
    private const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Canonical MIME emitted by <c>DocxReportExporter</c>.</summary>
    private const string DocxMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Canonical MIME emitted by <c>PdfReportExporter</c>.</summary>
    private const string PdfMimeType = "application/pdf";

    /// <summary>Fixed-instant clock used by the export-filename suffix.</summary>
    private sealed class FixedClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Helper that produces a fresh reporting-service substitute pre-wired with a tiny CSV.</summary>
    private static IReportingService NewReportingServiceWithCsvBody()
    {
        var svc = Substitute.For<IReportingService>();
        var payload = Encoding.UTF8.GetBytes("Code,Value\r\nA,1\r\n");
        svc.GenerateAsync(
                Arg.Any<string>(),
                Arg.Is<string>(s => s == "{}"),
                Arg.Is<ExportFormat>(f => f == ExportFormat.Csv),
                Arg.Any<CancellationToken>())
           .Returns(_ => Result<Stream>.Success(new MemoryStream(payload)));
        return svc;
    }

    /// <summary>Builds a selector substitute that returns the canonical envelope for the requested format.</summary>
    private static IReportExportSelector NewSelectorReturning(
        ReportExportFormat expectedFormat,
        string contentType,
        string extension)
    {
        var selector = Substitute.For<IReportExportSelector>();
        var bytes = Encoding.UTF8.GetBytes($"stub-{expectedFormat}");
        selector.ExportAsync(
                Arg.Is<ReportExportFormat>(f => f == expectedFormat),
                Arg.Any<ReportExportInputDto>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<ReportExportResultDto>.Success(
               new ReportExportResultDto(bytes, contentType, expectedFormat, extension)));
        return selector;
    }

    [Fact]
    public async Task ExportAsync_CsvFormat_ReturnsCsvMimeAndExtension()
    {
        var svc = NewReportingServiceWithCsvBody();
        var selector = NewSelectorReturning(ReportExportFormat.Csv, CsvMimeType, ".csv");
        var controller = new ReportsController(svc, selector, new FixedClock());

        var result = await controller.ExportAsync(
            "AUDIT_LOG", ReportExportFormat.Csv, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(CsvMimeType);
        file.FileDownloadName.Should().Be("AUDIT_LOG-20260524.csv");
        file.FileContents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_XlsxFormat_ReturnsXlsxMimeAndExtension()
    {
        var svc = NewReportingServiceWithCsvBody();
        var selector = NewSelectorReturning(ReportExportFormat.Xlsx, XlsxMimeType, ".xlsx");
        var controller = new ReportsController(svc, selector, new FixedClock());

        var result = await controller.ExportAsync(
            "AUDIT_LOG", ReportExportFormat.Xlsx, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(XlsxMimeType);
        file.FileDownloadName.Should().Be("AUDIT_LOG-20260524.xlsx");
        file.FileContents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_DocxFormat_ReturnsDocxMimeAndExtension()
    {
        var svc = NewReportingServiceWithCsvBody();
        var selector = NewSelectorReturning(ReportExportFormat.Docx, DocxMimeType, ".docx");
        var controller = new ReportsController(svc, selector, new FixedClock());

        var result = await controller.ExportAsync(
            "AUDIT_LOG", ReportExportFormat.Docx, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(DocxMimeType);
        file.FileDownloadName.Should().Be("AUDIT_LOG-20260524.docx");
        file.FileContents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_PdfFormat_ReturnsPdfMimeAndExtension()
    {
        var svc = NewReportingServiceWithCsvBody();
        var selector = NewSelectorReturning(ReportExportFormat.Pdf, PdfMimeType, ".pdf");
        var controller = new ReportsController(svc, selector, new FixedClock());

        var result = await controller.ExportAsync(
            "AUDIT_LOG", ReportExportFormat.Pdf, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(PdfMimeType);
        file.FileDownloadName.Should().Be("AUDIT_LOG-20260524.pdf");
        file.FileContents.Should().NotBeEmpty();
    }
}
