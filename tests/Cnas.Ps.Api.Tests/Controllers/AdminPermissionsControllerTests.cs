using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Permissions;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0673 / TOR CF 18.12 — unit tests for
/// <see cref="AdminPermissionsController"/>.
/// </summary>
public sealed class AdminPermissionsControllerTests
{
    /// <summary>Happy-path POST returns 200 + the DTO.</summary>
    [Fact]
    public async Task AssignAsync_Success_Returns200WithDto()
    {
        var svc = Substitute.For<IGranularPermissionService>();
        var dto = new GranularPermissionAssignmentDto(
            "SQID-1", RoleCodes.Decider, "Dossier", PermissionVerbs.View,
            new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc), "SQID-42");
        svc.AssignAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<GranularPermissionAssignmentDto>.Success(dto));
        var controller = new AdminPermissionsController(svc, new GranularPermissionAssignInputValidator());

        var result = await controller.AssignAsync(
            new GranularPermissionAssignInput(RoleCodes.Decider, "Dossier", PermissionVerbs.View),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>DELETE on missing assignment returns 404.</summary>
    [Fact]
    public async Task RevokeAsync_NotFound_Returns404()
    {
        var svc = Substitute.For<IGranularPermissionService>();
        svc.RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.NotFound, "row missing"));
        var controller = new AdminPermissionsController(svc, new GranularPermissionAssignInputValidator());

        var result = await controller.RevokeAsync("SQID-MISSING", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Anonymous → 401 + non-admin → 403 are enforced declaratively. Verify
    /// the controller's policy gate so a drive-by edit cannot relax it.
    /// </summary>
    [Fact]
    public void Controller_GatedBy_CnasAdminPolicy()
    {
        var attr = typeof(AdminPermissionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();
        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasAdmin);
    }

    /// <summary>List endpoint returns 200 + the list.</summary>
    [Fact]
    public async Task ListAsync_Success_Returns200()
    {
        var svc = Substitute.For<IGranularPermissionService>();
        var rows = new[]
        {
            new GranularPermissionAssignmentDto(
                "SQID-1", RoleCodes.Decider, "Dossier", PermissionVerbs.View,
                new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc), "SQID-42"),
        };
        svc.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<GranularPermissionAssignmentDto>>.Success(rows));
        var controller = new AdminPermissionsController(svc, new GranularPermissionAssignInputValidator());

        var result = await controller.ListAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rows);
    }

    /// <summary>
    /// An unknown <c>PermissionVerb</c> (not in the canonical
    /// <see cref="PermissionVerbs.All"/> set) MUST short-circuit at the
    /// validator and surface as <c>400 Bad Request</c> — the service layer is
    /// never consulted. Pins the validator-wired-into-the-controller contract.
    /// </summary>
    [Fact]
    public async Task AssignAsync_UnknownVerb_Returns400_AndDoesNotCallService()
    {
        var svc = Substitute.For<IGranularPermissionService>();
        var controller = new AdminPermissionsController(svc, new GranularPermissionAssignInputValidator());

        var result = await controller.AssignAsync(
            new GranularPermissionAssignInput(RoleCodes.Decider, "Dossier", "Sploosh"),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().AssignAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Empty <c>RoleCode</c> trips the validator's <c>NotEmpty</c> rule —
    /// surfaces as 400 and the service is never consulted.
    /// </summary>
    [Fact]
    public async Task AssignAsync_EmptyRoleCode_Returns400_AndDoesNotCallService()
    {
        var svc = Substitute.For<IGranularPermissionService>();
        var controller = new AdminPermissionsController(svc, new GranularPermissionAssignInputValidator());

        var result = await controller.AssignAsync(
            new GranularPermissionAssignInput(string.Empty, "Dossier", PermissionVerbs.View),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().AssignAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
