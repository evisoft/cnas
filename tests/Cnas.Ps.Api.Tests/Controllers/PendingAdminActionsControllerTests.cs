using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="PendingAdminActionsController"/> (R0058 / SEC 027 — 4-eyes
/// admin actions). Direct-construction style mirroring the rest of the controller suite;
/// the <see cref="IPendingAdminActionService"/> dependency is faked with NSubstitute.
/// Authorisation policy attribute presence (anonymous → 401, non-admin → 403) is verified
/// reflectively because the unit-test scope does not boot the HTTP pipeline.
/// </summary>
public sealed class PendingAdminActionsControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IPendingAdminActionService NewServiceMock() =>
        Substitute.For<IPendingAdminActionService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static PendingAdminActionsController NewController(IPendingAdminActionService svc) =>
        new(svc);

    // ─────────────────────── Authorisation surface ───────────────────────

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        // Anonymous → 401, non-admin → 403 are both enforced declaratively by the
        // [Authorize(Policy = CnasAdmin)] attribute that the ASP.NET pipeline checks
        // before any controller code runs. We verify the attribute is present here so
        // a future drive-by edit cannot silently downgrade the gate.
        var attr = typeof(PendingAdminActionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("the controller MUST be gated by an explicit Authorize policy");
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin);
    }

    // ─────────────────────── GET list ───────────────────────

    [Fact]
    public async Task Get_Admin_ReturnsPagedResult()
    {
        var svc = NewServiceMock();
        var paged = new PagedResult<PendingAdminActionItem>(
            Items:
            [
                new PendingAdminActionItem(
                    Id: "k3Gq9",
                    Operation: "DEMO.NOOP",
                    MakerUserId: "r8Wp2",
                    MakerRequestedAtUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc: new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc)),
            ],
            Page: 1, PageSize: 20, TotalCount: 1);
        svc.ListPendingAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
           .Returns(Result<PagedResult<PendingAdminActionItem>>.Success(paged));
        var controller = NewController(svc);

        var result = await controller.ListAsync(page: 1, pageSize: 20, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }

    // ─────────────────────── POST approve ───────────────────────

    [Fact]
    public async Task Approve_Maker_Approves_OwnAction_Returns403()
    {
        // Service signals self-approval — controller maps the stable code to 403.
        var svc = NewServiceMock();
        svc.ApproveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(
               ErrorCodes.MakerCheckerSelfApprovalForbidden,
               "Maker cannot approve their own action."));
        var controller = NewController(svc);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Maker cannot approve their own action.");
    }

    [Fact]
    public async Task Approve_Checker_Approves_Returns204_AndActionExecuted()
    {
        // Service returns success — controller returns 204 with no body. The execution
        // happens INSIDE the service (verified by the service-level test); the controller
        // simply forwards the Sqid.
        var svc = NewServiceMock();
        svc.ApproveAsync("k3Gq9", Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).ApproveAsync("k3Gq9", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_AlreadyDecided_Returns409()
    {
        // Idempotent guard: a second approve on a finalised row maps to 409.
        var svc = NewServiceMock();
        svc.ApproveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(
               ErrorCodes.MakerCheckerAlreadyDecided,
               "Action already decided."));
        var controller = NewController(svc);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Approve_Expired_Returns409()
    {
        // TTL elapsed before checker decided.
        var svc = NewServiceMock();
        svc.ApproveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(
               ErrorCodes.MakerCheckerExpired,
               "Action expired."));
        var controller = NewController(svc);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Approve_NotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.ApproveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Action not found."));
        var controller = NewController(svc);

        var result = await controller.ApproveAsync("BOGUS", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ─────────────────────── POST reject ───────────────────────

    [Fact]
    public async Task Reject_Checker_Rejects_Returns204_WithReasonPersisted()
    {
        // Service forwards the reason verbatim; controller returns 204 on success.
        var svc = NewServiceMock();
        svc.RejectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        var result = await controller.RejectAsync(
            "k3Gq9",
            new RejectAdminActionRequest("Insufficient justification."),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await svc.Received(1).RejectAsync(
            "k3Gq9",
            "Insufficient justification.",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reject_UnknownOperation_FromSubmit_PathReturns400()
    {
        // Defensive coverage for the unknown-operation code mapping. Reject never returns
        // this code in practice (operation is registered at submit time), but the
        // controller's StatusForCode table must still translate it.
        var svc = NewServiceMock();
        svc.RejectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(
               ErrorCodes.MakerCheckerUnknownOperation,
               "Unknown operation."));
        var controller = NewController(svc);

        var result = await controller.RejectAsync(
            "k3Gq9",
            new RejectAdminActionRequest("Whatever."),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
