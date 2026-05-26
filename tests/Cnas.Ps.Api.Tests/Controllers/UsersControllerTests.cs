using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="UsersController"/> using direct construction with a
/// NSubstitute mock of <see cref="IUserAdministrationService"/>. Mirrors the pattern in
/// <see cref="ContributorsControllerTests"/> — exercises controller branch logic without
/// booting the HTTP pipeline.
/// </summary>
public sealed class UsersControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IUserAdministrationService NewServiceMock() =>
        Substitute.For<IUserAdministrationService>();

    /// <summary>
    /// Builds the SUT around the supplied service. The R0059 state-machine service is a
    /// noop substitute for these tests because they exercise the legacy lock / role
    /// endpoints; UsersControllerStateTests covers the state endpoint separately.
    /// </summary>
    private static UsersController NewController(IUserAdministrationService svc) =>
        new(svc, Substitute.For<IUserAccountStateService>(),
            Substitute.For<IValidator<UserAccountStateBulkInputDto>>());

    [Fact]
    public async Task GrantRole_Success_Returns204()
    {
        // Arrange — the service signals success.
        var svc = NewServiceMock();
        svc.GrantRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var result = await controller.GrantRoleAsync(
            "UID", new GrantRoleRequest("cnas-decider"), CancellationToken.None);

        // Assert — controller returns 204 No Content on success per REST conventions.
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GrantRole_Forbidden_Returns403()
    {
        // Arrange — service signals Forbidden (caller lacks cnas-admin).
        var svc = NewServiceMock();
        svc.GrantRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GrantRoleAsync(
            "UID", new GrantRoleRequest("cnas-decider"), CancellationToken.None);

        // Assert — Forbidden maps to 403 ProblemDetails.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Caller lacks cnas-admin role.");
    }

    [Fact]
    public async Task Lock_NotFound_Returns404()
    {
        // Arrange — the requested user does not exist.
        var svc = NewServiceMock();
        svc.LockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "User not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.LockAsync("MISSING", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_Success_Returns200WithPagedBody()
    {
        // Arrange — service returns a single-page list with one Sqid-encoded id.
        var svc = NewServiceMock();
        var paged = new PagedResult<UserListItem>(
            // R0059 — replaced legacy IsLocked bool with the State enum's string form.
            Items: [new UserListItem("k3Gq9", "Ion Popescu", "ion@example.md", "Active", ["cnas-user"])],
            Page: 1,
            PageSize: 20,
            TotalCount: 1);
        svc.ListAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<UserListItem>>.Success(paged));
        var controller = NewController(svc);

        // Act
        var result = await controller.ListAsync(page: 1, pageSize: 20, CancellationToken.None);

        // Assert — 200 with the paged body forwarded verbatim.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }
}
