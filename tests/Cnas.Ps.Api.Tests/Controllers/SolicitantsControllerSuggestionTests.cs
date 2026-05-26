using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0525 / TOR CF 03.08 — verifies the SolicitantsController surfaces the structured
/// <see cref="SearchSuggestionDto"/> list emitted by the service when the result set is
/// over the suggestion threshold. The wire payload carries an explicit
/// <c>Suggestions</c> array on the response envelope so the UI can render refinement
/// prompts without a second round-trip.
/// </summary>
public sealed class SolicitantsControllerSuggestionTests
{
    private static SolicitantsController NewController(ISolicitantService svc) => new(svc);

    [Fact]
    public async Task Search_ReturnsSuggestions_OnResponse_WhenServiceEmitsThem()
    {
        var svc = Substitute.For<ISolicitantService>();
        var page = new PagedResult<SolicitantListItem>(
            Items: Array.Empty<SolicitantListItem>(),
            Page: 1,
            PageSize: 20,
            TotalCount: 750);
        svc.SearchAsync(Arg.Any<SolicitantListQueryInput>(), Arg.Any<QbeFilter?>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<SolicitantListItem>>.Success(page));
        IReadOnlyList<SearchSuggestionDto> suggestions = new[]
        {
            new SearchSuggestionDto("AddStatusFilter", "IsActive", "TooBroad", "true"),
        };
        svc.LastSuggestions.Returns(suggestions);

        var controller = NewController(svc);
        var body = new SolicitantSearchInput();
        var result = await controller.SearchAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should().BeOfType<SolicitantSearchOutput>().Subject;
        envelope.Page.TotalCount.Should().Be(750);
        envelope.Suggestions.Should().ContainSingle()
            .Which.Code.Should().Be("AddStatusFilter");
    }
}
