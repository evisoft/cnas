using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0193 / TOR SEC 052 — controller-shape tests for
/// <see cref="AuditExplorerController"/>. Direct-construction style; verifies
/// the HTTP mapping from the service-layer <see cref="Result{T}"/> envelope to
/// the canonical ProblemDetails shape.
/// </summary>
public sealed class AuditExplorerControllerTests
{
    private static IAuditExplorerService NewServiceMock() =>
        Substitute.For<IAuditExplorerService>();

    private static AuditExplorerController NewController(IAuditExplorerService svc) => new(svc);

    [Fact]
    public void Controller_RequiresCnasAdminRole()
    {
        var attr = typeof(AuditExplorerController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Roles.Should().Be("cnas-admin");
    }

    [Fact]
    public async Task Search_ServiceSuccess_Returns200WithPageDto()
    {
        var svc = NewServiceMock();
        var page = new AuditLogPageDto(
            Items: new[]
            {
                new AuditLogRowDto(
                    Id: "SQID-1",
                    CreatedAtUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
                    EventCode: "USER.LOGIN.SUCCESS",
                    Severity: "Information",
                    ActorUserSqid: "SQID-42",
                    ResourceType: "UserProfile",
                    ResourceSqid: "SQID-7",
                    DetailsJson: "{}",
                    PrevHashHex: "genesis",
                    RowHashHex: "abcd1234"),
            },
            TotalCount: 1,
            AppliedSuggestions: Array.Empty<string>());
        svc.SearchAsync(Arg.Any<AuditLogSearchInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<AuditLogPageDto>.Success(page));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(
            new AuditLogSearchInput(),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Search_BudgetTooBroad_Returns422_WithVerdict()
    {
        var svc = NewServiceMock();
        svc.SearchAsync(Arg.Any<AuditLogSearchInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<AuditLogPageDto>.Failure(ErrorCodes.QueryTooBroad, "narrow"));
        svc.LastBudgetVerdict.Returns(new QueryBudgetVerdict(
            Allowed: false,
            EstimatedRowCount: 5000,
            Budget: 1000,
            Registry: QueryBudgetRegistries.AuditLog,
            Hints: Array.Empty<RefinementHint>()));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(new AuditLogSearchInput(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("budget");
    }

    [Fact]
    public async Task Export_Csv_HappyPath_Returns200_WithFile()
    {
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<AuditLogSearchInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Success(new GridExportResult(
                Content: new byte[] { 1, 2, 3 },
                ContentType: "text/csv; charset=utf-8",
                SuggestedFileName: "AuditLogs-20260521.csv")));

        var controller = NewController(svc);
        var result = await controller.ExportAsync(
            format: "csv",
            input: new AuditLogSearchInput(),
            cancellationToken: CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().StartWith("text/csv");
        file.FileDownloadName.Should().Be("AuditLogs-20260521.csv");
    }

    [Fact]
    public async Task Export_RendererMissing_Returns501()
    {
        var svc = NewServiceMock();
        svc.ExportAsync(
                Arg.Any<AuditLogSearchInput>(),
                Arg.Any<ExportFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<GridExportResult>.Failure(
                ErrorCodes.ExportFormatNotSupported,
                "xlsx not wired"));

        var controller = NewController(svc);
        var result = await controller.ExportAsync(
            format: "xlsx",
            input: new AuditLogSearchInput(),
            cancellationToken: CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["format"].Should().Be("xlsx");
    }

    [Fact]
    public async Task Import_NotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.ImportArchiveAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Result<AuditArchiveImportSummaryDto>.Failure(ErrorCodes.NotFound, "no such file"));

        var controller = NewController(svc);
        var result = await controller.ImportArchiveAsync("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Import_Success_Returns200_WithSummary()
    {
        var svc = NewServiceMock();
        var summary = new AuditArchiveImportSummaryDto(
            RowsImported: 3,
            RowsSkipped: 0,
            FirstUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            LastUtc: new DateTime(2026, 5, 21, 10, 5, 0, DateTimeKind.Utc),
            ArchiveKey: "file-1");
        svc.ImportArchiveAsync("file-1", Arg.Any<CancellationToken>())
            .Returns(Result<AuditArchiveImportSummaryDto>.Success(summary));

        var controller = NewController(svc);
        var result = await controller.ImportArchiveAsync("file-1", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(summary);
    }
}
