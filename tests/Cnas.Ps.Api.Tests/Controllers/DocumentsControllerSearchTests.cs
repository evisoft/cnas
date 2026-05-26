using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0671 continuation — controller-level tests for
/// <c>POST /api/documents/search</c>. Verifies the success path returns the page
/// DTO and the budget-refusal path returns the canonical 422 ProblemDetails.
/// </summary>
public sealed class DocumentsControllerSearchTests
{
    private static DocumentsController NewController(IDocumentService svc) => new(svc);

    [Fact]
    public async Task Search_ServiceReturnsSuccess_Returns200_WithPageDto()
    {
        var svc = Substitute.For<IDocumentService>();
        var page = new DocumentsListPageDto(
            Items: new[]
            {
                new DocumentListItemDto(
                    Id: "SQID-1",
                    OwnerEntityType: "Dossier",
                    OwnerEntitySqid: "SQID-99",
                    DocumentKind: "Attachment",
                    FileName: "scan.pdf",
                    MimeType: "application/pdf",
                    SizeBytes: 1024,
                    CreatedAtUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    IssuedByUserSqid: "SQID-7"),
            },
            TotalCount: 1);
        svc.ListAsync(Arg.Any<DocumentsListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentsListPageDto>.Success(page));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(new DocumentsListInput(), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<DocumentsListPageDto>()
            .Which.Items.Should().ContainSingle().Which.Id.Should().Be("SQID-1");
    }

    [Fact]
    public async Task Search_ServiceReturnsQueryTooBroad_Returns422_WithBudgetExtension()
    {
        var svc = Substitute.For<IDocumentService>();
        svc.ListAsync(Arg.Any<DocumentsListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentsListPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                "narrow your filter"));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(new DocumentsListInput(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("budget");
    }
}
