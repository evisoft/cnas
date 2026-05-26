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
/// Unit tests for <see cref="InsuredPersonsController"/> using direct construction with a
/// NSubstitute mock of <see cref="IInsuredPersonService"/>. Same approach as
/// <c>ContributorsControllerTests</c> — covers controller branch logic without booting
/// the full HTTP pipeline.
/// </summary>
public sealed class InsuredPersonsControllerTests
{
    /// <summary>Deterministic clock value used by happy-path output samples.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static IInsuredPersonService NewServiceMock() => Substitute.For<IInsuredPersonService>();

    /// <summary>Stub clock returning a fixed UTC instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a controller wired with the supplied service.</summary>
    private static InsuredPersonsController NewController(IInsuredPersonService svc) =>
        new(svc, NewSelectorStub(), new StubClock(ClockNow));

    /// <summary>Builds a controller wired with the supplied service + selector.</summary>
    private static InsuredPersonsController NewController(
        IInsuredPersonService svc,
        IReportExportSelector selector) =>
        new(svc, selector, new StubClock(ClockNow));

    /// <summary>Returns a no-op selector stub for tests that never hit it.</summary>
    private static IReportExportSelector NewSelectorStub() =>
        Substitute.For<IReportExportSelector>();

    /// <summary>Builds a selector substitute that returns a canonical envelope for the requested format.</summary>
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

    /// <summary>Default valid registration payload used by happy-path tests.</summary>
    private static InsuredPersonRegistrationInput SampleInput() =>
        new("2000123456782", "Popescu", "Ion", "Vasilevici", new DateOnly(1980, 5, 12));

    /// <summary>Default valid insured-person output for Get assertions.</summary>
    private static InsuredPersonOutput SampleOutput(string id = "abc12") =>
        new(
            Id: id,
            Idnp: "2000123456782",
            LastName: "Popescu",
            FirstName: "Ion",
            Patronymic: "Vasilevici",
            BirthDate: new DateOnly(1980, 5, 12),
            IsDeceased: false,
            DateOfDeath: null,
            RegisteredAtUtc: ClockNow,
            LastRspSyncUtc: null);

    [Fact]
    public async Task Register_ServiceReturnsSuccess_Returns201()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<InsuredPersonRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success("xyz99"));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert: 201 Created pointing at GetAsync with the new id in route values.
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(InsuredPersonsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["id"].Should().Be("xyz99");
        created.Value.Should().Be("xyz99");
    }

    [Fact]
    public async Task Register_ServiceReturnsConflict_Returns409()
    {
        // Arrange: service signals duplicate IDNP.
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<InsuredPersonRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.Conflict, "Insured person already registered."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert: 409 ProblemDetails with the human message.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Insured person already registered.");
    }

    [Fact]
    public async Task Register_ServiceReturnsValidationFailure_Returns400()
    {
        // Arrange: service signals a bad IDNP at the value-object layer.
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<InsuredPersonRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.InvalidIdnp, "IDNP checksum digit does not match."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("IDNP checksum digit does not match.");
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<InsuredPersonOutput>.Failure(ErrorCodes.NotFound, "Insured person not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("zzz99", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_Found_Returns200WithBody()
    {
        // Arrange
        var svc = NewServiceMock();
        var output = SampleOutput("abc12");
        svc.GetByIdAsync(Arg.Is<string>(s => s == "abc12"), Arg.Any<CancellationToken>())
           .Returns(Result<InsuredPersonOutput>.Success(output));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("abc12", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(output);
    }

    [Fact]
    public async Task MarkDeceased_HappyPath_Returns200()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.MarkDeceasedAsync(
                Arg.Is<string>(s => s == "abc12"),
                Arg.Is<DateOnly>(d => d == new DateOnly(2026, 5, 10)),
                Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.MarkDeceasedAsync(
            "abc12",
            new InsuredPersonsController.MarkDeceasedRequest(new DateOnly(2026, 5, 10)),
            CancellationToken.None);

        // Assert: controller returns Ok() on success.
        result.Should().BeOfType<OkResult>();
    }

    // ════════════════════ R0610 / TOR CF 12.01 — register-browser export ════════════════════
    //
    // Iter 125 adds a `format` query parameter to SearchAsync. When the caller
    // passes `format=xlsx` or `format=pdf` the controller projects the search
    // page into ReportExportInputDto and dispatches through IReportExportSelector;
    // the response becomes a FileContentResult instead of the paged JSON body.

    /// <summary>Canonical MIME emitted by <c>XlsxReportExporter</c>.</summary>
    private const string XlsxMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Canonical MIME emitted by <c>PdfReportExporter</c>.</summary>
    private const string PdfMimeType = "application/pdf";

    /// <summary>Builds a deterministic single-row page result for the export tests.</summary>
    private static PagedResult<InsuredPersonListItem> SamplePage() =>
        new(
            Items: [new InsuredPersonListItem("SQID-1", "2000123456782", "Popescu Ion Vasilevici", false)],
            TotalCount: 1,
            Page: 1,
            PageSize: 20);

    [Fact]
    public async Task Search_XlsxFormat_DispatchesThroughSelectorAndReturnsXlsxContentType()
    {
        var svc = NewServiceMock();
        svc.SearchAsync(Arg.Any<string?>(), Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<InsuredPersonListItem>>.Success(SamplePage()));
        var selector = NewSelectorReturning(ReportExportFormat.Xlsx, XlsxMimeType, ".xlsx");
        var controller = NewController(svc, selector);

        var result = await controller.SearchAsync(
            query: null, page: 1, pageSize: 20,
            format: ReportExportFormat.Xlsx,
            cancellationToken: CancellationToken.None);

        var file = result.Result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(XlsxMimeType);
        file.FileDownloadName.Should().EndWith(".xlsx");
        file.FileContents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Search_PdfFormat_DispatchesThroughSelectorAndReturnsPdfContentType()
    {
        var svc = NewServiceMock();
        svc.SearchAsync(Arg.Any<string?>(), Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<InsuredPersonListItem>>.Success(SamplePage()));
        var selector = NewSelectorReturning(ReportExportFormat.Pdf, PdfMimeType, ".pdf");
        var controller = NewController(svc, selector);

        var result = await controller.SearchAsync(
            query: null, page: 1, pageSize: 20,
            format: ReportExportFormat.Pdf,
            cancellationToken: CancellationToken.None);

        var file = result.Result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(PdfMimeType);
        file.FileDownloadName.Should().EndWith(".pdf");
        file.FileContents.Should().NotBeEmpty();
    }
}
