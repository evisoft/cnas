using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="SavedSearchesController"/> (R0165 / CF 03.06). Direct-
/// construction style mirroring the rest of the controller suite; the
/// <see cref="ISavedSearchService"/> dependency is faked with NSubstitute and the
/// authorisation-policy attribute presence is verified reflectively.
/// </summary>
public sealed class SavedSearchesControllerTests
{
    private static ISavedSearchService NewServiceMock() => Substitute.For<ISavedSearchService>();

    private static SavedSearchesController NewController(ISavedSearchService svc) => new(svc);

    // ─────────────────────── Authorisation surface ───────────────────────

    [Fact]
    public void Get_Anonymous_Returns401()
    {
        // Anonymous → 401 / non-authenticated → 403 are declaratively enforced by the
        // [Authorize(Policy = CnasUser)] attribute. We verify the attribute is present so
        // a future drive-by edit cannot silently downgrade the gate.
        var attr = typeof(SavedSearchesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("the controller MUST be gated by an explicit Authorize policy");
        attr!.Policy.Should().Be(AuthorizationComposition.CnasUser);
    }

    // ─────────────────────── POST create ───────────────────────

    [Fact]
    public async Task Post_ValidBody_Returns201_AndCreatedRouteHeader()
    {
        var svc = NewServiceMock();
        svc.CreateAsync(Arg.Any<SavedSearchCreateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success("k3Gq9"));
        var controller = NewController(svc);

        var result = await controller.CreateAsync(
            new SavedSearchCreateInput("Contributors", "n1", "{}", IsShared: false),
            CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(SavedSearchesController.GetAsync));
        created.Value.Should().Be("k3Gq9");
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["sqid"].Should().Be("k3Gq9");
    }

    // ─────────────────────── PUT — non-owner update ───────────────────────

    [Fact]
    public async Task Put_PrivateRowAsNonOwner_Returns403()
    {
        // Service surfaces Forbidden — the controller MUST map it to 403, not the default 400.
        var svc = NewServiceMock();
        svc.UpdateAsync(Arg.Any<string>(), Arg.Any<SavedSearchUpdateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.Forbidden, "Only the owner may update a saved search."));
        var controller = NewController(svc);

        var result = await controller.UpdateAsync(
            "k3Gq9",
            new SavedSearchUpdateInput("n1", "{}", IsShared: false),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Only the owner may update a saved search.");
    }

    // ─────────────────────── DELETE ───────────────────────

    [Fact]
    public async Task Delete_OwnRow_Returns204()
    {
        var svc = NewServiceMock();
        svc.DeleteAsync("k3Gq9", Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        var result = await controller.DeleteAsync("k3Gq9", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).DeleteAsync("k3Gq9", Arg.Any<CancellationToken>());
    }

    // ─────────────────────── GET list ───────────────────────

    [Fact]
    public async Task List_MissingRegistry_Returns400()
    {
        // Controller short-circuits before reaching the service when the registry query
        // parameter is missing. Verifies the controller-level guard (separate from the
        // service-level ValidationFailed branch).
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.ListAsync(registry: null, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Service should NOT have been called — the gate fired in the controller.
        await svc.DidNotReceiveWithAnyArgs().ListAsync(default!, default);
    }

    // ─────────────────────── POST /share — R0524 ───────────────────────

    [Fact]
    public async Task Share_OwnerFlipsToShared_Returns200_WithUpdatedDto()
    {
        // R0524: the share endpoint returns 200 with the updated SavedSearchItem so the
        // caller doesn't need a follow-up GET to refresh the row.
        var svc = NewServiceMock();
        var updated = new SavedSearchItem(
            Id: "k3Gq9",
            Registry: "Contributors",
            Name: "n1",
            FilterJson: "{}",
            IsShared: true,
            OwnerUserId: "SQID-OWNER",
            SharingScope: nameof(SavedSearchSharingScope.Shared),
            SharedWithGroupCode: null);
        svc.ShareAsync(Arg.Any<string>(), Arg.Any<SavedSearchShareInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<SavedSearchItem>.Success(updated));
        var controller = NewController(svc);

        var result = await controller.ShareAsync(
            "k3Gq9",
            new SavedSearchShareInput(nameof(SavedSearchSharingScope.Shared), null),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeSameAs(updated);
    }

    // ─────────────────────── GET /accessible — R0524 ───────────────────────

    [Fact]
    public async Task Accessible_ReturnsList_OnSuccess()
    {
        var svc = NewServiceMock();
        var item = new SavedSearchItem(
            Id: "k3Gq9",
            Registry: "Contributors",
            Name: "shared-by-other",
            FilterJson: "{}",
            IsShared: true,
            OwnerUserId: "SQID-OTHER",
            SharingScope: nameof(SavedSearchSharingScope.Shared),
            SharedWithGroupCode: null);
        svc.ListAccessibleAsync("Contributors", Arg.Any<CancellationToken>())
           .Returns(new[] { item } as IReadOnlyList<SavedSearchItem>);
        var controller = NewController(svc);

        var result = await controller.AccessibleAsync(registry: "Contributors", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<SavedSearchItem>>()
          .Which.Should().ContainSingle(i => i.Name == "shared-by-other");
    }
}
