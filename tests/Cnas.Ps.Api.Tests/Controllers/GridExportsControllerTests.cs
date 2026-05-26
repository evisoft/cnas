using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0226 / TOR UI 013 — controller-level unit tests for
/// <see cref="GridExportsController"/>. The controller delegates to
/// <see cref="ISolicitantGridExportService"/> and translates the service's
/// <see cref="Result{T}"/> envelope into the appropriate HTTP shape:
/// <list type="bullet">
///   <item>200 + file response on success;</item>
///   <item>422 ProblemDetails with the budget verdict on
///         <see cref="ErrorCodes.QueryTooBroad"/>;</item>
///   <item>422 ProblemDetails on <see cref="ErrorCodes.ExportTooLarge"/>;</item>
///   <item>501 ProblemDetails on <see cref="ErrorCodes.ExportFormatNotSupported"/>.</item>
/// </list>
/// </summary>
public sealed class GridExportsControllerTests
{
    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static ISolicitantGridExportService NewServiceMock() =>
        Substitute.For<ISolicitantGridExportService>();

    /// <summary>Builds a controller wired with the supplied service.</summary>
    private static GridExportsController NewController(ISolicitantGridExportService svc) => new(svc);

    /// <summary>Convenience builder for a successful renderer result.</summary>
    private static GridExportResult NewExportResult(string suffix) =>
        new(
            Content: new byte[] { 1, 2, 3 },
            ContentType: suffix switch
            {
                "csv"  => "text/csv; charset=utf-8",
                "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "pdf"  => "application/pdf",
                _      => "application/octet-stream",
            },
            SuggestedFileName: $"Solicitants-20260521-1042.{suffix}");

    [Fact]
    public async Task ExportSolicitants_Csv_HappyPath_Returns200_WithFile()
    {
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<SolicitantListQueryInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Success(NewExportResult("csv")));
        var controller = NewController(svc);

        var result = await controller.ExportSolicitantsAsync(
            format: "csv",
            input: new SolicitantListQueryInput(),
            language: "ro",
            cancellationToken: CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().StartWith("text/csv");
        file.FileDownloadName.Should().Be("Solicitants-20260521-1042.csv");
    }

    [Fact]
    public async Task ExportSolicitants_XlsxRendererMissing_Returns501_WithFormatExtension()
    {
        // The service reports the renderer is unavailable; controller surfaces 501.
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<SolicitantListQueryInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Failure(
                ErrorCodes.ExportFormatNotSupported,
                "xlsx not wired"));
        var controller = NewController(svc);

        var result = await controller.ExportSolicitantsAsync(
            format: "xlsx",
            input: new SolicitantListQueryInput(),
            language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(501);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(501);
        problem.Extensions["format"].Should().Be("xlsx");
    }

    [Fact]
    public async Task ExportSolicitants_BudgetGateRefuses_Returns422_WithBudgetVerdict()
    {
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<SolicitantListQueryInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Failure(
                ErrorCodes.QueryTooBroad,
                "narrow your filter"));
        svc.LastBudgetVerdict.Returns(new QueryBudgetVerdict(
            Allowed: false,
            EstimatedRowCount: 8000,
            Budget: 5000,
            Registry: QueryBudgetRegistries.Solicitant,
            Hints: new[]
            {
                new RefinementHint("Q", RefinementHintSeverity.Required, RefinementHintReasons.AddFreeTextFilter),
            }));
        var controller = NewController(svc);

        var result = await controller.ExportSolicitantsAsync(
            format: "csv",
            input: new SolicitantListQueryInput(),
            language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(422);
        problem.Extensions.Should().ContainKey("budget");
        var dto = problem.Extensions["budget"].Should().BeOfType<QueryBudgetVerdictDto>().Subject;
        dto.Registry.Should().Be(QueryBudgetRegistries.Solicitant);
        dto.EstimatedRowCount.Should().Be(8000);
        dto.Budget.Should().Be(5000);
    }

    [Fact]
    public async Task ExportSolicitants_TooLarge_Returns422_WithRowCountExtension()
    {
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<SolicitantListQueryInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Failure(
                ErrorCodes.ExportTooLarge,
                "too many rows: 60000"));
        var controller = NewController(svc);

        var result = await controller.ExportSolicitantsAsync(
            format: "csv",
            input: new SolicitantListQueryInput(),
            language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("rowCount");
    }

    [Fact]
    public async Task ExportSolicitants_UnknownFormat_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.ExportSolicitantsAsync(
            format: "doc",
            input: new SolicitantListQueryInput(),
            language: "ro",
            cancellationToken: CancellationToken.None);

        // BadRequestObjectResult is the framework's typed 400 result; assert
        // by status code (it inherits from ObjectResult) rather than by the
        // exact type.
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
    }
}
