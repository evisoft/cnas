using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0167 — controller-level unit tests for <see cref="SolicitantsController"/>. The
/// controller delegates to <see cref="ISolicitantService"/> through the budget pipe;
/// these tests assert the 422 ProblemDetails contract and the
/// <c>extensions["budget"]</c> payload shape.
/// </summary>
public sealed class SolicitantsControllerTests
{
    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static ISolicitantService NewServiceMock() => Substitute.For<ISolicitantService>();

    /// <summary>Builds a controller wired with the supplied service.</summary>
    private static SolicitantsController NewController(ISolicitantService svc) => new(svc);

    [Fact]
    public async Task List_ServiceReturnsSuccess_Returns200()
    {
        var svc = NewServiceMock();
        var page = new PagedResult<SolicitantListItem>(
            Items: new[] { new SolicitantListItem("SQID-1", "Ion Popescu", "NaturalPerson", DateTime.UtcNow) },
            Page: 1,
            PageSize: 20,
            TotalCount: 1);
        svc.ListAsync(Arg.Any<SolicitantListQueryInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<SolicitantListItem>>.Success(page));
        var controller = NewController(svc);

        var result = await controller.ListAsync(
            query: "Popescu", createdFromUtc: null, createdToUtc: null,
            page: 1, pageSize: 20, cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task List_ServiceReturnsQueryTooBroad_Returns422_WithProblemDetailsBudgetExtension()
    {
        var svc = NewServiceMock();
        svc.ListAsync(Arg.Any<SolicitantListQueryInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<SolicitantListItem>>.Failure(
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
                new RefinementHint("CreatedFromUtc", RefinementHintSeverity.Suggested, RefinementHintReasons.AddDateFilter),
            }));
        var controller = NewController(svc);

        var result = await controller.ListAsync(
            query: null, createdFromUtc: null, createdToUtc: null,
            page: 1, pageSize: 20, cancellationToken: CancellationToken.None);

        // 422 ObjectResult carrying a ProblemDetails with the budget extension populated.
        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/queries/too-broad");
        problem.Status.Should().Be(422);
        problem.Extensions.Should().ContainKey("budget");
        var dto = problem.Extensions["budget"].Should().BeOfType<QueryBudgetVerdictDto>().Subject;
        dto.Registry.Should().Be(QueryBudgetRegistries.Solicitant);
        dto.EstimatedRowCount.Should().Be(8000);
        dto.Budget.Should().Be(5000);
        dto.Hints.Should().HaveCount(2);
        dto.Hints[0].FieldName.Should().Be("Q");
        dto.Hints[0].Severity.Should().Be(RefinementHintSeverity.Required);
        dto.Hints[1].FieldName.Should().Be("CreatedFromUtc");
        dto.Hints[1].Severity.Should().Be(RefinementHintSeverity.Suggested);
    }

    [Fact]
    public async Task VerdictDto_ContainsNoRawNumericIds()
    {
        // Sqid invariant (CLAUDE.md RULE 3) — the verdict DTO must NOT carry any
        // raw long / int IDs. Reflect the DTO record and verify each public property is
        // either string or a primitive numeric describing row counts (EstimatedRowCount /
        // Budget) — not an Id.
        var dto = new QueryBudgetVerdictDto(
            Registry: "Solicitant",
            EstimatedRowCount: 8000,
            Budget: 5000,
            Hints: new[] { new QueryBudgetRefinementHintDto("Q", "Required", "AddFreeTextFilter") });

        var props = typeof(QueryBudgetVerdictDto).GetProperties();
        foreach (var p in props)
        {
            p.Name.Should().NotEndWith("Id");
            p.Name.Should().NotEndWith("Ids");
        }
        var hintProps = typeof(QueryBudgetRefinementHintDto).GetProperties();
        foreach (var p in hintProps)
        {
            p.Name.Should().NotEndWith("Id");
            p.Name.Should().NotEndWith("Ids");
        }
        dto.Hints.Should().ContainSingle();
    }
}
