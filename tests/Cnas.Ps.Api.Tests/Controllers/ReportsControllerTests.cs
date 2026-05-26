using System.Text;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ReportsController"/>. Same direct-construction pattern as
/// <see cref="ContributorsControllerTests"/> — exercises controller branch logic without
/// booting the HTTP pipeline. The reporting service is faked with NSubstitute and the
/// generated stream is a small in-memory byte array (no I/O).
/// </summary>
public sealed class ReportsControllerTests
{
    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static IReportingService NewServiceMock() => Substitute.For<IReportingService>();

    /// <summary>Helper that produces a fresh export-selector substitute.</summary>
    private static IReportExportSelector NewSelectorMock() => Substitute.For<IReportExportSelector>();

    /// <summary>Fixed-instant clock used by the export-filename suffix.</summary>
    private sealed class FixedClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static ReportsController NewController(IReportingService svc) =>
        new(svc, NewSelectorMock(), new FixedClock());

    /// <summary>Builds the SUT around all three collaborators (used by the export tests).</summary>
    private static ReportsController NewController(
        IReportingService svc,
        IReportExportSelector selector) => new(svc, selector, new FixedClock());

    [Fact]
    public async Task ListAvailable_ServiceSuccess_Returns200WithCatalog()
    {
        // Arrange — the service returns a two-entry catalogue.
        var svc = NewServiceMock();
        IReadOnlyList<ReportCatalogEntryOutput> entries =
        [
            new("AUDIT_LOG", "Jurnal de audit", "Журнал аудита", "Audit log"),
            new("CONTRIBUTORS", "Plătitori", "Плательщики", "Contributors"),
        ];
        svc.ListAvailableAsync(Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<ReportCatalogEntryOutput>>.Success(entries));
        var controller = NewController(svc);

        // Act
        var result = await controller.ListAvailableAsync(CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(entries);
    }

    [Fact]
    public async Task ListAvailable_ServiceFailure_Returns400()
    {
        // Arrange — generic failure path.
        var svc = NewServiceMock();
        svc.ListAvailableAsync(Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<ReportCatalogEntryOutput>>.Failure(
               ErrorCodes.Internal, "Boom."));
        var controller = NewController(svc);

        // Act
        var result = await controller.ListAvailableAsync(CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Generate_HappyPath_ReturnsFileStreamWithExpectedContentType()
    {
        // Arrange — service returns a CSV stream.
        var svc = NewServiceMock();
        var payload = Encoding.UTF8.GetBytes("Status,Count\r\nApproved,1\r\n");
        var stream = new MemoryStream(payload);
        svc.GenerateAsync(
                Arg.Is<string>(c => c == "APPLICATIONS_BY_STATUS"),
                Arg.Any<string>(),
                Arg.Is<ExportFormat>(f => f == ExportFormat.Csv),
                Arg.Any<CancellationToken>())
           .Returns(Result<Stream>.Success(stream));
        var controller = NewController(svc);

        // Act
        var body = new ReportGenerateRequest(
            new Dictionary<string, string?> { ["passportCode"] = "SP-A" },
            ExportFormat.Csv);
        var result = await controller.GenerateAsync(
            "APPLICATIONS_BY_STATUS", body, CancellationToken.None);

        // Assert — FileStreamResult with text/csv and the same stream
        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("text/csv");
        file.FileDownloadName.Should().Contain("APPLICATIONS_BY_STATUS");
        file.FileDownloadName.Should().EndWith(".csv");
        file.FileStream.Should().BeSameAs(stream);
    }

    [Fact]
    public async Task Generate_UnknownReportCode_Returns404()
    {
        // Arrange — service reports NotFound for an unknown code.
        var svc = NewServiceMock();
        svc.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<Stream>.Failure(ErrorCodes.NotFound, "Unknown report code"));
        var controller = NewController(svc);

        // Act
        var body = new ReportGenerateRequest(null, ExportFormat.Csv);
        var result = await controller.GenerateAsync("BOGUS", body, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Generate_ValidationFailure_Returns400()
    {
        // Arrange — service reports a parameter validation failure.
        var svc = NewServiceMock();
        svc.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<Stream>.Failure(ErrorCodes.ValidationFailed, "Bad params"));
        var controller = NewController(svc);

        // Act
        var body = new ReportGenerateRequest(null, ExportFormat.Csv);
        var result = await controller.GenerateAsync("AUDIT_LOG", body, CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Bad params");
    }

    [Fact]
    public async Task Generate_NullBody_Returns400()
    {
        // The controller must guard against a null body even though [FromBody] normally
        // populates it — ArgumentNullException maps to 400 via the ASP.NET filter.
        var controller = NewController(NewServiceMock());

        // Act + Assert
        await FluentActions.Awaiting(() =>
                controller.GenerateAsync("AUDIT_LOG", null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Generate_XlsxFormat_UsesXlsxContentType()
    {
        // Arrange
        var svc = NewServiceMock();
        var stream = new MemoryStream([0x50, 0x4B]); // ZIP magic bytes (XLSX)
        svc.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<ExportFormat>(f => f == ExportFormat.Xlsx),
                Arg.Any<CancellationToken>())
           .Returns(Result<Stream>.Success(stream));
        var controller = NewController(svc);

        // Act
        var body = new ReportGenerateRequest(null, ExportFormat.Xlsx);
        var result = await controller.GenerateAsync("AUDIT_LOG", body, CancellationToken.None);

        // Assert
        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileDownloadName.Should().EndWith(".xlsx");
    }

    [Fact]
    public async Task Generate_PdfFormat_UsesPdfContentType()
    {
        // Arrange
        var svc = NewServiceMock();
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        svc.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<ExportFormat>(f => f == ExportFormat.Pdf),
                Arg.Any<CancellationToken>())
           .Returns(Result<Stream>.Success(stream));
        var controller = NewController(svc);

        // Act
        var body = new ReportGenerateRequest(null, ExportFormat.Pdf);
        var result = await controller.GenerateAsync("AUDIT_LOG", body, CancellationToken.None);

        // Assert
        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().EndWith(".pdf");
    }
}
