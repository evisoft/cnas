using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2270 / TOR SEC 023-024 — controller-level tests for
/// <see cref="UserGroupsController"/>.
/// </summary>
public sealed class UserGroupsControllerTests
{
    /// <summary>Canonical sample DTO returned by the service mock.</summary>
    private static UserGroupDto SampleDto() => new(
        Id: "GRP-1",
        Code: "OFFICE_A",
        DisplayName: "Office A",
        Description: null,
        Kind: nameof(UserGroupKind.OrganizationalUnit),
        Status: nameof(UserGroupStatus.Active),
        Roles: ["ROLE_A"],
        DirectMemberCount: 0,
        DirectChildCount: 0,
        EffectiveRoleCount: 1);

    /// <summary>R2270 — POST /api/user-groups returns 201 on success.</summary>
    [Fact]
    public async Task CreateAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IUserGroupService>();
        var resolver = Substitute.For<IUserGroupRoleResolver>();
        svc.CreateAsync(Arg.Any<UserGroupCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserGroupDto>.Success(SampleDto()));
        var controller = new UserGroupsController(svc, resolver);

        var input = new UserGroupCreateInputDto(
            Code: "OFFICE_A",
            DisplayName: "Office A",
            Description: null,
            Kind: nameof(UserGroupKind.OrganizationalUnit),
            Roles: ["ROLE_A"]);

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<UserGroupDto>().Subject;
        dto.Id.Should().Be("GRP-1");
    }

    /// <summary>R2270 — PUT /api/user-groups/{sqid} returns 200 on success.</summary>
    [Fact]
    public async Task ModifyAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IUserGroupService>();
        var resolver = Substitute.For<IUserGroupRoleResolver>();
        svc.ModifyAsync(Arg.Any<string>(), Arg.Any<UserGroupModifyInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserGroupDto>.Success(SampleDto()));
        var controller = new UserGroupsController(svc, resolver);

        var input = new UserGroupModifyInputDto(
            DisplayName: "New",
            Description: null,
            Kind: null,
            Roles: null,
            ChangeReason: "rename");

        var result = await controller.ModifyAsync("GRP-1", input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>R2270 — POST /api/user-groups/{parent}/children/{child} returns 200 on success.</summary>
    [Fact]
    public async Task AddChildAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IUserGroupService>();
        var resolver = Substitute.For<IUserGroupRoleResolver>();
        svc.AddChildAsync("GRP-1", "GRP-2", Arg.Any<CancellationToken>())
            .Returns(Result<UserGroupDto>.Success(SampleDto()));
        var controller = new UserGroupsController(svc, resolver);

        var result = await controller.AddChildAsync("GRP-1", "GRP-2", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>R2270 — GET /api/user-groups/users/{userSqid}/effective-roles returns 200.</summary>
    [Fact]
    public async Task GetEffectiveRoles_Returns200()
    {
        var svc = Substitute.For<IUserGroupService>();
        var resolver = Substitute.For<IUserGroupRoleResolver>();

        var fakeSqid = Substitute.For<ISqidService>();
        fakeSqid.TryDecode(Arg.Any<string>()).Returns(Result<long>.Success(42));

        resolver.ResolveEffectiveRolesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserGroupEffectiveRolesDto>.Success(new UserGroupEffectiveRolesDto(
                UserSqid: "USR-42",
                Roles: [new UserGroupEffectiveRoleDto("ROLE_A", ["OFFICE_A"])])));

        var controller = new UserGroupsController(svc, resolver) { Sqid = fakeSqid };

        var result = await controller.GetEffectiveRolesAsync("USR-42", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var dto = ok.Value.Should().BeOfType<UserGroupEffectiveRolesDto>().Subject;
        dto.Roles.Should().HaveCount(1);
    }
}
