using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for the <c>POST /api/users/{id}/state</c> endpoint on
/// <see cref="UsersController"/> (R0059 / SEC 016 — account state machine). Direct
/// construction with NSubstitute fakes of the two collaborating services; the
/// authorisation policy attribute is verified reflectively because the unit-test scope
/// does not boot the HTTP pipeline (declarative <c>[Authorize]</c> attributes are
/// enforced by ASP.NET before any controller code runs).
/// </summary>
public sealed class UsersControllerStateTests
{
    private static IUserAdministrationService NewAdminSvcMock() =>
        Substitute.For<IUserAdministrationService>();

    private static IUserAccountStateService NewStateSvcMock() =>
        Substitute.For<IUserAccountStateService>();

    private static IValidator<UserAccountStateBulkInputDto> NewBulkValidatorMock(bool valid = true)
    {
        var validator = Substitute.For<IValidator<UserAccountStateBulkInputDto>>();
        var result = valid
            ? new ValidationResult()
            : new ValidationResult(new[] { new ValidationFailure("UserSqids", "bad") });
        validator.ValidateAsync(Arg.Any<UserAccountStateBulkInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return validator;
    }

    private static UsersController NewController(
        IUserAdministrationService? admin = null,
        IUserAccountStateService? state = null,
        IValidator<UserAccountStateBulkInputDto>? bulkValidator = null) =>
        new(admin ?? NewAdminSvcMock(),
            state ?? NewStateSvcMock(),
            bulkValidator ?? NewBulkValidatorMock());

    // ─────────────────────── Authorisation surface ───────────────────────

    [Fact]
    public void Anonymous_Returns401_ByControllerAuthorizationAttribute()
    {
        // Anonymous → 401 is enforced declaratively by the [Authorize] attribute on the
        // controller class; verify the attribute is present so a future drive-by edit
        // cannot silently downgrade the gate. The controller has a single class-level
        // Authorize attribute scoped to the CnasAdmin policy.
        var attr = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("the controller MUST be gated by an explicit Authorize policy");
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin,
            "non-admin callers (including unauthenticated) must be rejected before any action runs");
    }

    [Fact]
    public void NonAdmin_Returns403_ByControllerAuthorizationAttribute()
    {
        // Same as above — the [Authorize(Policy = CnasAdmin)] attribute rejects both
        // unauthenticated (401) and authenticated-but-non-admin (403) callers. The test
        // duplication is intentional: when the attribute regresses, both expectations
        // fire so a maintainer sees the full impact in CI.
        var attr = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin);
    }

    // ─────────────────────── Happy paths ───────────────────────

    [Fact]
    public async Task Admin_AllowedTransition_Returns204()
    {
        var state = NewStateSvcMock();
        state.ChangeStateAsync(
                "UID", UserAccountState.Suspended, "Pending HR investigation",
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(state: state);

        var result = await controller.ChangeStateAsync(
            "UID",
            new ChangeUserStateRequest("Suspended", "Pending HR investigation"),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await state.Received(1).ChangeStateAsync(
            "UID", UserAccountState.Suspended, "Pending HR investigation",
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Failure mappings ───────────────────────

    [Fact]
    public async Task Admin_DisallowedTransition_Returns409()
    {
        // The transition matrix rejected the request (e.g. Disabled → Locked). The
        // controller maps the stable code to 409 Conflict — a state-machine violation
        // is a conflict with the resource's current state, not an auth failure.
        var state = NewStateSvcMock();
        state.ChangeStateAsync(
                Arg.Any<string>(), Arg.Any<UserAccountState>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure(
                ErrorCodes.UserAccountStateTransitionForbidden,
                "Cannot transition from Disabled to Locked."));
        var controller = NewController(state: state);

        var result = await controller.ChangeStateAsync(
            "UID",
            new ChangeUserStateRequest("Locked", null),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
            .Which.Detail.Should().Be("Cannot transition from Disabled to Locked.");
    }

    [Fact]
    public async Task Admin_UnknownUserSqid_Returns404()
    {
        var state = NewStateSvcMock();
        state.ChangeStateAsync(
                Arg.Any<string>(), Arg.Any<UserAccountState>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.NotFound, "User not found."));
        var controller = NewController(state: state);

        var result = await controller.ChangeStateAsync(
            "MISSING",
            new ChangeUserStateRequest("Suspended", null),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ─────────────────────── R2263 bulk endpoints ───────────────────────

    [Fact]
    public async Task BulkSuspend_ServiceSuccess_Returns200WithResult()
    {
        var state = NewStateSvcMock();
        var expected = new UserAccountStateBulkResultDto(
            TotalRequested: 2,
            Succeeded: 2,
            Failed: 0,
            Failures: Array.Empty<UserAccountStateBulkResultRowDto>());
        state.BulkSuspendAsync(
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<UserAccountStateBulkResultDto>.Success(expected));
        var controller = NewController(state: state);

        var result = await controller.BulkSuspendAsync(
            new UserAccountStateBulkInputDto(["U1", "U2"], "compliance"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(expected);
    }

    [Fact]
    public async Task BulkSuspend_ValidationFailure_Returns400()
    {
        var validator = NewBulkValidatorMock(valid: false);
        var controller = NewController(bulkValidator: validator);

        var result = await controller.BulkSuspendAsync(
            new UserAccountStateBulkInputDto(Array.Empty<string>(), "bad"),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task BulkSuspend_AuthorizationAttribute_GatesByCnasAdmin()
    {
        // Verifies the [Authorize(Policy = CnasAdmin)] gate covers the bulk endpoint —
        // the attribute lives on the controller class, not the action method, so the
        // assertion mirrors UsersControllerStateTests.Anonymous_Returns401_ByControllerAuthorizationAttribute.
        var attr = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task Admin_InvalidStateName_Returns400()
    {
        // The body carried a string that does not parse to any UserAccountState value —
        // the controller must reject at the boundary BEFORE calling into the service.
        // The state service must not have been invoked.
        var state = NewStateSvcMock();
        var controller = NewController(state: state);

        var result = await controller.ChangeStateAsync(
            "UID",
            new ChangeUserStateRequest("Bogus", "irrelevant"),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await state.DidNotReceiveWithAnyArgs().ChangeStateAsync(
            default!, default, default, default);
    }
}
