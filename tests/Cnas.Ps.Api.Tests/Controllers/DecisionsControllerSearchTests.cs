using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0671 continuation — controller-level tests for <c>POST /api/decisions/search</c>.
/// </summary>
public sealed class DecisionsControllerSearchTests
{
    private static DecisionsController NewController(IDecisionWorkflowService svc) => new(svc);

    [Fact]
    public async Task Search_ServiceReturnsSuccess_Returns200_WithPageDto()
    {
        var svc = Substitute.For<IDecisionWorkflowService>();
        var page = new DecisionsListPageDto(
            Items: new[]
            {
                new DecisionListItemDto(
                    Id: "SQID-D1",
                    ServiceApplicationSqid: "SQID-A1",
                    Status: "Approved",
                    DraftedAtUtc: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    FinalisedAtUtc: new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                    DraftedByUserSqid: "SQID-7",
                    DossierNumber: "D-2026-00001"),
            },
            TotalCount: 1);
        svc.ListAsync(Arg.Any<DecisionsListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<DecisionsListPageDto>.Success(page));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(new DecisionsListInput(), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<DecisionsListPageDto>()
            .Which.Items.Should().ContainSingle().Which.Id.Should().Be("SQID-D1");
    }

    [Fact]
    public async Task Search_ServiceReturnsQueryTooBroad_Returns422_WithBudgetExtension()
    {
        var svc = Substitute.For<IDecisionWorkflowService>();
        svc.ListAsync(Arg.Any<DecisionsListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<DecisionsListPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                "narrow your filter"));

        var controller = NewController(svc);
        var result = await controller.SearchAsync(new DecisionsListInput(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("budget");
    }
}
