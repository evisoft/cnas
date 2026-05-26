using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0163 — controller-level tests for the QBE-aware <c>POST /api/solicitants/search</c>
/// endpoint. Verifies the wire-DTO round-trip, the ProblemDetails fieldName extension,
/// and the success path.
/// </summary>
public sealed class SolicitantsControllerSearchTests
{
    /// <summary>Builds a controller wired with the supplied service substitute.</summary>
    private static SolicitantsController NewController(ISolicitantService svc) => new(svc);

    [Fact]
    public async Task Search_ServiceReturnsSuccess_Returns200_WithSqidIds()
    {
        var svc = Substitute.For<ISolicitantService>();
        var page = new PagedResult<SolicitantListItem>(
            Items: new[]
            {
                new SolicitantListItem("SQID-7", "Ion Popescu", "NaturalPerson", DateTime.UtcNow),
            },
            Page: 1,
            PageSize: 20,
            TotalCount: 1);
        svc.SearchAsync(Arg.Any<SolicitantListQueryInput>(), Arg.Any<QbeFilter?>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<SolicitantListItem>>.Success(page));
        var controller = NewController(svc);

        var body = new SolicitantSearchInput(
            Qbe: new QbeFilterDto("AND", new[]
            {
                new QbeConditionDto("Email", "Equals", "ion@example.com"),
            }));
        var result = await controller.SearchAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        // R0525 — Search now returns SolicitantSearchOutput (page + suggestions).
        var envelope = ok.Value.Should().BeOfType<SolicitantSearchOutput>().Subject;
        envelope.Page.Items.Should().ContainSingle().Which.Id.Should().Be("SQID-7");
        envelope.Suggestions.Should().NotBeNull();
    }

    [Fact]
    public async Task Search_ServiceReturnsQbeFieldNotQueryable_Returns400_WithFieldNameExtension()
    {
        var svc = Substitute.For<ISolicitantService>();
        svc.SearchAsync(Arg.Any<SolicitantListQueryInput>(), Arg.Any<QbeFilter?>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<SolicitantListItem>>.Failure(
               ErrorCodes.QbeFieldNotQueryable,
               "Field 'BadColumn' is not queryable for registry 'Solicitant'."));
        var controller = NewController(svc);

        var body = new SolicitantSearchInput(
            Qbe: new QbeFilterDto("AND", new[]
            {
                new QbeConditionDto("BadColumn", "Equals", "x"),
            }));
        var result = await controller.SearchAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("fieldName");
        problem.Extensions["fieldName"].Should().Be("BadColumn");
        problem.Extensions.Should().ContainKey("errorCode");
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.QbeFieldNotQueryable);
    }
}
