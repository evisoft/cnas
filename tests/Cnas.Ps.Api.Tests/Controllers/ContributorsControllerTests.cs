using System.Text;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ContributorsController"/> using direct construction with a
/// NSubstitute mock of <see cref="IContributorService"/>. Same approach as
/// <c>ApplicationsControllerTests</c> — covers controller branch logic without booting
/// the full HTTP pipeline.
/// </summary>
public sealed class ContributorsControllerTests
{
    /// <summary>Deterministic clock value used when the controller defaults atUtc.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning a fixed UTC instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static IContributorService NewServiceMock() => Substitute.For<IContributorService>();

    /// <summary>
    /// Builds a stub <see cref="ISqidService"/> that round-trips an internal id through
    /// the textual encoding <c>"SQID-{id}"</c> for deterministic assertions, and decodes
    /// the inverse. Tests that need other behaviour override the returned substitute.
    /// </summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        // Default decode: rejects anything not of the form "SQID-{n}". Per-test setup
        // overrides this when the controller needs to decode a specific id.
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Builds a controller wired with the supplied service and a fixed clock.</summary>
    private static ContributorsController NewController(IContributorService svc) =>
        new(svc, new StubClock(ClockNow), NewSqidStub(), NewSelectorStub());

    /// <summary>Builds a controller wired with the supplied service, clock, and an explicit Sqid stub.</summary>
    private static ContributorsController NewController(IContributorService svc, ISqidService sqids) =>
        new(svc, new StubClock(ClockNow), sqids, NewSelectorStub());

    /// <summary>Builds a controller wired with the supplied service + selector stub.</summary>
    private static ContributorsController NewController(
        IContributorService svc,
        IReportExportSelector selector) =>
        new(svc, new StubClock(ClockNow), NewSqidStub(), selector);

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
    private static ContributorRegistrationInput SampleInput() =>
        new("1003600012346", "SRL Exemplu", "1170", "47111");

    /// <summary>Default valid contributor output for Get/IsInsured assertions.</summary>
    private static ContributorOutput SampleOutput(string id = "abc12") =>
        new(
            Id: id,
            Idno: "1003600012346",
            Denumire: "SRL Exemplu",
            CfojCode: "1170",
            CaemCode: "47111",
            IsInsolvent: false,
            RegisteredAtUtc: ClockNow,
            DeregisteredAtUtc: null);

    [Fact]
    public async Task Register_ServiceReturnsSuccess_Returns201()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<ContributorRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success("xyz99"));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert: 201 Created pointing at GetAsync with the new id in route values.
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(ContributorsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["id"].Should().Be("xyz99");
        created.Value.Should().Be("xyz99");
    }

    [Fact]
    public async Task Register_ServiceReturnsConflict_Returns409()
    {
        // Arrange: service signals duplicate IDNO.
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<ContributorRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.Conflict, "Contributor already registered."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert: 409 ProblemDetails with the human message.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Contributor already registered.");
    }

    [Fact]
    public async Task Register_ServiceReturnsValidationFailure_Returns400()
    {
        // Arrange: service signals a bad IDNO at the value-object layer.
        var svc = NewServiceMock();
        svc.RegisterAsync(Arg.Any<ContributorRegistrationInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.InvalidIdno, "IDNO checksum digit does not match."));
        var controller = NewController(svc);

        // Act
        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("IDNO checksum digit does not match.");
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ContributorOutput>.Failure(ErrorCodes.NotFound, "Contributor not found."));
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
           .Returns(Result<ContributorOutput>.Success(output));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("abc12", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(output);
    }

    [Fact]
    public async Task IsInsured_HappyPath_Returns200()
    {
        // Arrange
        var svc = NewServiceMock();
        var payload = new IsInsuredResult("1003600012346", true, ClockNow);
        svc.IsInsuredAsync(
                Arg.Is<string>(s => s == "1003600012346"),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<IsInsuredResult>.Success(payload));
        var controller = NewController(svc);

        // Act
        var result = await controller.IsInsuredAsync("1003600012346", ClockNow, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(payload);
    }

    // ════════════════════ R0305 / TOR Annex 1 — BP endpoint coverage ════════════════════

    /// <summary>
    /// R0305 / BP 1.2 — PUT <c>/api/contributors/{sqid}/attributes</c> returns 200
    /// with the updated <see cref="ContributorOutput"/> on a successful service result.
    /// </summary>
    [Fact]
    public async Task UpdateAttributes_HappyPath_Returns200()
    {
        var svc = NewServiceMock();
        var updated = SampleOutput("abc12") with { Denumire = "New SRL" };
        svc.UpdateAttributesAsync(
                Arg.Is<long>(id => id == 42L),
                Arg.Any<ContributorAttributesUpdateDto>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<ContributorOutput>.Success(updated));
        var sqids = NewSqidStub();
        sqids.TryDecode("SQID-42").Returns(Result<long>.Success(42L));
        var controller = NewController(svc, sqids);

        var result = await controller.UpdateAttributesAsync(
            "SQID-42",
            new ContributorAttributesUpdateDto("New SRL", null, null),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(updated);
    }

    /// <summary>
    /// R0305 / BP 1.5 — POST <c>/api/contributors/{dup}/merge-into/{surv}</c> returns
    /// 204 No Content when the service merge succeeds.
    /// </summary>
    [Fact]
    public async Task MergeDuplicates_HappyPath_Returns204()
    {
        var svc = NewServiceMock();
        svc.MergeDuplicatesAsync(
                Arg.Is<long>(id => id == 1L),
                Arg.Is<long>(id => id == 2L),
                Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var sqids = NewSqidStub();
        sqids.TryDecode("SQID-1").Returns(Result<long>.Success(1L));
        sqids.TryDecode("SQID-2").Returns(Result<long>.Success(2L));
        var controller = NewController(svc, sqids);

        var result = await controller.MergeDuplicatesAsync("SQID-1", "SQID-2", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
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
    private static PagedResult<ContributorListItem> SamplePage() =>
        new(
            Items: [new ContributorListItem("SQID-1", "1003600012346", "SRL Exemplu", false)],
            TotalCount: 1,
            Page: 1,
            PageSize: 20);

    [Fact]
    public async Task Search_XlsxFormat_DispatchesThroughSelectorAndReturnsXlsxContentType()
    {
        var svc = NewServiceMock();
        svc.SearchAsync(Arg.Any<string?>(), Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<ContributorListItem>>.Success(SamplePage()));
        var selector = NewSelectorReturning(ReportExportFormat.Xlsx, XlsxMimeType, ".xlsx");
        var controller = NewController(svc, selector);

        var result = await controller.SearchAsync(
            q: null, page: 1, pageSize: 20,
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
           .Returns(Result<PagedResult<ContributorListItem>>.Success(SamplePage()));
        var selector = NewSelectorReturning(ReportExportFormat.Pdf, PdfMimeType, ".pdf");
        var controller = NewController(svc, selector);

        var result = await controller.SearchAsync(
            q: null, page: 1, pageSize: 20,
            format: ReportExportFormat.Pdf,
            cancellationToken: CancellationToken.None);

        var file = result.Result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(PdfMimeType);
        file.FileDownloadName.Should().EndWith(".pdf");
        file.FileContents.Should().NotBeEmpty();
    }
}
