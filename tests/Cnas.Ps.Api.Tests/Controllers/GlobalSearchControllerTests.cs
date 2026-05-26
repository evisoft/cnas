using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="GlobalSearchController"/> (R0160 / R0161 /
/// TOR CF 03.03). The <see cref="IGlobalSearchService"/> dependency is faked
/// with NSubstitute; the controller-level authorisation attribute is verified
/// reflectively.
/// </summary>
public sealed class GlobalSearchControllerTests
{
    /// <summary>Returns a fresh service mock used by each controller-under-test.</summary>
    /// <returns>An NSubstitute fake.</returns>
    private static IGlobalSearchService NewServiceMock() => Substitute.For<IGlobalSearchService>();

    /// <summary>Constructs the controller-under-test with the supplied service stub.</summary>
    /// <param name="svc">Backing legacy search service stub.</param>
    /// <returns>A wired controller.</returns>
    private static GlobalSearchController NewController(IGlobalSearchService svc)
    {
        var unified = Substitute.For<IUnifiedDataSearchService>();
        var catalog = new Cnas.Ps.Application.Search.StaticSearchCriteriaCatalog();
        return new(svc, unified, catalog);
    }

    // ─────────────────────── authorisation surface ───────────────────────

    /// <summary>
    /// The controller MUST be gated by an explicit
    /// <see cref="AuthorizationComposition.CnasUser"/> policy so an accidental
    /// drive-by edit cannot silently downgrade the gate.
    /// </summary>
    [Fact]
    public void Controller_Carries_CnasUser_AuthorizePolicy()
    {
        var attr = typeof(GlobalSearchController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasUser);
    }

    // ─────────────────────── happy path ───────────────────────

    /// <summary>200 with the service-supplied result on the happy path.</summary>
    [Fact]
    public async Task Get_ValidQuery_Returns200_WithResult()
    {
        var svc = NewServiceMock();
        var expected = new GlobalSearchResultDto(
            TotalHits: 1,
            Results: new[]
            {
                new GlobalSearchHitDto(
                    Domain: GlobalSearchDomains.Applications,
                    Sqid: "k3Gq9",
                    Title: "REF-ALPHA-001",
                    Snippet: "REF-ALPHA-001",
                    Rank: 0.42),
            },
            Skip: 0,
            Take: 20);
        svc.SearchAsync(Arg.Any<GlobalSearchInputDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<GlobalSearchResultDto>.Success(expected));

        var controller = NewController(svc);

        var result = await controller.SearchAsync(
            q: "alpha",
            domains: "applications",
            skip: 0,
            take: 20,
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(expected);
    }

    // ─────────────────────── empty query ───────────────────────

    /// <summary>Empty query short-circuits to 400 ProblemDetails — the service is never called.</summary>
    [Fact]
    public async Task Get_EmptyQuery_Returns400_AndDoesNotCallService()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.SearchAsync(
            q: "   ",
            domains: null,
            skip: 0,
            take: 20,
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    // ─────────────────────── empty result set ───────────────────────

    /// <summary>An empty result set still returns 200 with TotalHits=0.</summary>
    [Fact]
    public async Task Get_EmptyResults_Returns200_WithZeroHits()
    {
        var svc = NewServiceMock();
        var empty = new GlobalSearchResultDto(
            TotalHits: 0,
            Results: Array.Empty<GlobalSearchHitDto>(),
            Skip: 0,
            Take: 20);
        svc.SearchAsync(Arg.Any<GlobalSearchInputDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<GlobalSearchResultDto>.Success(empty));

        var controller = NewController(svc);

        var result = await controller.SearchAsync(
            q: "no-match",
            domains: null,
            skip: 0,
            take: 20,
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<GlobalSearchResultDto>().Subject;
        dto.TotalHits.Should().Be(0);
        dto.Results.Should().BeEmpty();
    }

    // ─────────────────────── service validation failure surfaces 400 ───────────────────────

    /// <summary>Service-layer VALIDATION_FAILED maps to 400 ProblemDetails.</summary>
    [Fact]
    public async Task Get_ServiceValidationFailed_Returns400_ProblemDetails()
    {
        var svc = NewServiceMock();
        svc.SearchAsync(Arg.Any<GlobalSearchInputDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<GlobalSearchResultDto>.Failure(
               ErrorCodes.ValidationFailed,
               "Query is required."));

        var controller = NewController(svc);

        var result = await controller.SearchAsync(
            q: "alpha",
            domains: null,
            skip: 0,
            take: 20,
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
            .Which.Detail.Should().Be("Query is required.");
    }
}
