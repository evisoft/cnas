using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0502 / R0504 / R0505 — controller-level unit tests for
/// <see cref="PublicCatalogController"/>. The controller delegates to
/// <see cref="IPublicCatalogService"/>; these tests assert the 422 ProblemDetails
/// shape on too-broad queries, the 501 ProblemDetails for the deferred PDF/XLSX
/// formats, and the happy-path CSV file response.
/// </summary>
public sealed class PublicCatalogControllerTests
{
    private static IPublicCatalogService NewServiceMock() => Substitute.For<IPublicCatalogService>();

    /// <summary>
    /// Builds a controller with sane default collaborators. The default CAPTCHA
    /// policy returns <c>false</c> (no challenge required) so the pre-existing
    /// test bodies continue to assert their service-layer behaviour without
    /// having to mint a token. CAPTCHA-specific tests pass their own pair.
    /// </summary>
    private static PublicCatalogController NewController(
        IPublicCatalogService svc,
        ICaptchaPolicyEvaluator? policy = null,
        ICaptchaChallengeService? captcha = null)
    {
        var pol = policy ?? Substitute.For<ICaptchaPolicyEvaluator>();
        if (policy is null)
        {
            pol.RequireCaptcha(Arg.Any<PublicCatalogListQueryDto?>()).Returns(false);
        }
        var cap = captcha ?? Substitute.For<ICaptchaChallengeService>();
        if (captcha is null)
        {
            cap.IsRecentlyVerifiedAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        }
        return new PublicCatalogController(svc, pol, cap)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
    }

    [Fact]
    public async Task List_ServiceReturnsSuccess_Returns200()
    {
        var svc = NewServiceMock();
        var page = new PagedResult<PublicCatalogListItemDto>(
            Items: new[]
            {
                new PublicCatalogListItemDto(
                    Id: "SQID-1",
                    Code: "SP-1",
                    Name: "Pensia",
                    Description: "Detalii",
                    Category: "PENSIONS",
                    Version: 1,
                    UpdatedAtUtc: DateTime.UtcNow),
            },
            Page: 1,
            PageSize: 50,
            TotalCount: 1);
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Success(page));
        var controller = NewController(svc);

        var result = await controller.ListAsync(q: "pen", category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro", cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task List_ServiceReturnsQueryTooBroad_Returns422_WithProblemDetailsBudgetExtension()
    {
        var svc = NewServiceMock();
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Failure(
               ErrorCodes.QueryTooBroad,
               "too broad"));
        svc.LastBudgetVerdict.Returns(new QueryBudgetVerdict(
            Allowed: false,
            EstimatedRowCount: 2000,
            Budget: 1000,
            Registry: QueryBudgetRegistries.PublicCatalog,
            Hints: new[]
            {
                new RefinementHint("Q", RefinementHintSeverity.Required, RefinementHintReasons.AddFreeTextFilter),
                new RefinementHint("Category", RefinementHintSeverity.Suggested, RefinementHintReasons.AddIdentifierFilter),
            }));
        var controller = NewController(svc);

        var result = await controller.ListAsync(q: null, category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro", cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/queries/too-broad");
        problem.Status.Should().Be(422);
        problem.Extensions.Should().ContainKey("budget");
        var dto = problem.Extensions["budget"].Should().BeOfType<QueryBudgetVerdictDto>().Subject;
        dto.Registry.Should().Be(QueryBudgetRegistries.PublicCatalog);
        dto.Budget.Should().Be(1000);
        dto.EstimatedRowCount.Should().Be(2000);
        dto.Hints.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExportCsv_ServiceReturnsSuccess_ReturnsFileWithCsvContentType()
    {
        var svc = NewServiceMock();
        var payload = System.Text.Encoding.UTF8.GetBytes("Code,Name\nA,B\n");
        svc.ExportCsvAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<byte[]>.Success(payload));
        var controller = NewController(svc);

        var result = await controller.ExportCsvAsync(
            q: "any", category: null, sort: "Relevance", language: "ro",
            cancellationToken: CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().StartWith("text/csv");
        file.FileContents.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task ExportCsv_ServiceReturnsTooBroad_Returns422()
    {
        var svc = NewServiceMock();
        svc.ExportCsvAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<byte[]>.Failure(ErrorCodes.QueryTooBroad, "too broad"));
        svc.LastBudgetVerdict.Returns(new QueryBudgetVerdict(
            Allowed: false,
            EstimatedRowCount: 2000,
            Budget: 1000,
            Registry: QueryBudgetRegistries.PublicCatalog,
            Hints: Array.Empty<RefinementHint>()));
        var controller = NewController(svc);

        var result = await controller.ExportCsvAsync(
            q: null, category: null, sort: "Relevance", language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
    }

    [Fact]
    public void ExportPdf_Returns501ProblemDetails()
    {
        var controller = NewController(NewServiceMock());

        var result = controller.ExportPdf();

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(501);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/exports/not-implemented");
        problem.Status.Should().Be(501);
        problem.Extensions["format"].Should().Be("pdf");
    }

    [Fact]
    public void ExportXlsx_Returns501ProblemDetails()
    {
        var controller = NewController(NewServiceMock());

        var result = controller.ExportXlsx();

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(501);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/exports/not-implemented");
        problem.Extensions["format"].Should().Be("xlsx");
    }
}
