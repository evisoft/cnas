using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Integration.Search;

/// <summary>
/// R0501 / TOR CF 01.04 — controller-level integration tests for the
/// metadata-driven criteria endpoint surfaced by
/// <see cref="GlobalSearchController.GetCriteriaAsync"/>. Drives the controller
/// action directly (the test fixture in this project does not host a
/// <c>WebApplicationFactory</c>; matching pattern used by
/// <c>PublicEndpointsNoPiiTests</c>).
/// </summary>
public sealed class SearchCriteriaEndpointTests
{
    /// <summary>Wires a controller with the production catalogue + stub search service.</summary>
    /// <returns>A new controller-under-test.</returns>
    private static GlobalSearchController NewController()
    {
        var search = Substitute.For<IGlobalSearchService>();
        var unified = Substitute.For<IUnifiedDataSearchService>();
        var catalog = new StaticSearchCriteriaCatalog();
        return new GlobalSearchController(search, unified, catalog);
    }

    /// <summary>200 + descriptor list for the applications domain.</summary>
    [Fact]
    public async Task GetCriteria_KnownDomain_Returns200_WithDescriptors()
    {
        var controller = NewController();

        var result = await controller.GetCriteriaAsync(GlobalSearchDomains.Applications);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<System.Collections.Generic.IReadOnlyList<SearchCriterionDescriptor>>().Subject;
        list.Should().NotBeEmpty();
        list.Select(c => c.Field).Should().Contain("code");
    }

    /// <summary>404 ProblemDetails when the domain is not in the catalogue.</summary>
    [Fact]
    public async Task GetCriteria_UnknownDomain_Returns404()
    {
        var controller = NewController();

        var result = await controller.GetCriteriaAsync("nope-not-a-domain");

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}