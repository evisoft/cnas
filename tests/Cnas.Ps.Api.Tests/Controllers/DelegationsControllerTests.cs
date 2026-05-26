using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — unit tests for
/// <see cref="DelegationsController"/>. Direct-construction style mirroring the rest
/// of the controller suite; the <see cref="IDelegationLifecycleService"/> dependency
/// is faked with NSubstitute. Authentication presence (anonymous → 401 via the
/// pipeline) is verified reflectively because the unit-test scope does not boot the
/// HTTP pipeline.
/// </summary>
public sealed class DelegationsControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IDelegationLifecycleService NewServiceMock() =>
        Substitute.For<IDelegationLifecycleService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static DelegationsController NewController(IDelegationLifecycleService svc)
    {
        var grantValidator = Substitute.For<IValidator<DelegationGrantInputDto>>();
        grantValidator.ValidateAsync(Arg.Any<DelegationGrantInputDto>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());
        var revokeValidator = Substitute.For<IValidator<DelegationGrantRevokeInputDto>>();
        revokeValidator.ValidateAsync(Arg.Any<DelegationGrantRevokeInputDto>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());
        var ctrl = new DelegationsController(svc, grantValidator, revokeValidator);
        // Some action methods consult HttpContext.User; install a minimal context so
        // the controller does not NRE when reading claims.
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return ctrl;
    }

    // ─────────────────────── Authorisation surface ───────────────────────

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        // Anonymous → 401 is enforced declaratively by the [Authorize] attribute
        // that the ASP.NET pipeline checks before any controller code runs. We
        // verify the attribute is present so a future drive-by edit cannot silently
        // downgrade the gate.
        var attr = typeof(DelegationsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("the controller MUST be gated by an [Authorize] attribute");
    }

    // ─────────────────────── POST grant ───────────────────────

    [Fact]
    public async Task Grant_HappyPath_Returns201_WithDto()
    {
        var svc = NewServiceMock();
        var now = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);
        var dto = new DelegationGrantDto(
            Id: "SQID-1",
            GrantorUserId: "SQID-G",
            DelegateeUserId: "SQID-D",
            ValidFromUtc: now,
            ValidToUtc: now.AddDays(30),
            SuspendsGrantorRights: false,
            Scope: "approve.executory_documents",
            GrantedAtUtc: now,
            RevokedAtUtc: null,
            RevokeReason: null);
        svc.GrantAsync(
                Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<DelegationGrantDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GrantAsync(
            new DelegationGrantInputDto(
                "SQID-D", now, now.AddDays(30), false, "approve.executory_documents"),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
        created.Location.Should().Be("/api/delegations/SQID-1");
    }

    [Fact]
    public async Task Grant_ValidationFailure_Returns400()
    {
        var svc = NewServiceMock();
        svc.GrantAsync(
                Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<DelegationGrantDto>.Failure(
                ErrorCodes.ValidationFailed, "Window too long."));
        var controller = NewController(svc);

        var result = await controller.GrantAsync(
            new DelegationGrantInputDto(
                "SQID-D",
                new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc),
                false, "x"),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ─────────────────────── DELETE revoke ───────────────────────

    [Fact]
    public async Task Revoke_HappyPath_Returns204()
    {
        var svc = NewServiceMock();
        svc.RevokeAsync("SQID-1", "Project closed.", Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(svc);

        var result = await controller.RevokeAsync(
            "SQID-1",
            new DelegationGrantRevokeInputDto("Project closed."),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Revoke_NotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.RevokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.NotFound, "Grant not found."));
        var controller = NewController(svc);

        var result = await controller.RevokeAsync(
            "BOGUS",
            new DelegationGrantRevokeInputDto("Reason text here."),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Revoke_NotGrantor_Returns403()
    {
        var svc = NewServiceMock();
        svc.RevokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.Forbidden, "Only grantor may revoke."));
        var controller = NewController(svc);

        var result = await controller.RevokeAsync(
            "SQID-1",
            new DelegationGrantRevokeInputDto("Reason text here."),
            CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// iter-149 / Fix 12 — when wired with the REAL FluentValidation validator
    /// the controller MUST reject a tampered DTO (window > 90 days) with 400
    /// and MUST NOT forward to the service. Proves the validator is actually
    /// invoked at the controller boundary rather than being a dead dependency.
    /// </summary>
    [Fact]
    public async Task Grant_TamperedWindow_RejectedByValidator_Returns400_WithoutCallingService()
    {
        var svc = NewServiceMock();
        var grantValidator = new DelegationGrantInputValidator();
        var revokeValidator = new DelegationGrantRevokeInputValidator();
        var controller = new DelegationsController(svc, grantValidator, revokeValidator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var now = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
        var tampered = new DelegationGrantInputDto(
            DelegateeSqid: "SQID-D",
            ValidFromUtc: now,
            ValidToUtc: now.AddDays(180), // Exceeds the 90-day cap.
            SuspendsGrantorRights: false,
            Scope: "approve.something");

        var result = await controller.GrantAsync(tampered, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().GrantAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// iter-149 / Fix 12 — the revoke validator gate must be wired. A tampered
    /// DTO (too-short reason) must surface as 400 without touching the service.
    /// </summary>
    [Fact]
    public async Task Revoke_TamperedShortReason_RejectedByValidator_Returns400_WithoutCallingService()
    {
        var svc = NewServiceMock();
        var grantValidator = new DelegationGrantInputValidator();
        var revokeValidator = new DelegationGrantRevokeInputValidator();
        var controller = new DelegationsController(svc, grantValidator, revokeValidator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var tampered = new DelegationGrantRevokeInputDto("ab"); // 2 chars — below the 3-char minimum.

        var result = await controller.RevokeAsync("SQID-1", tampered, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
