using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Identity;

/// <summary>
/// R2270 / TOR SEC 023-024 — tests for <see cref="UserGroupRoleResolver"/>.
/// Covers direct grants, nested-ancestor resolution, disabled-group exclusion,
/// and the empty case.
/// </summary>
public sealed class UserGroupRoleResolverTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-user-groups-resolver-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub that round-trips "GRP-{id}" / "USR-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"GRP-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && (s.StartsWith("GRP-", StringComparison.Ordinal) || s.StartsWith("USR-", StringComparison.Ordinal))
                && long.TryParse(s[4..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Builds the SUT.</summary>
    private static UserGroupRoleResolver NewResolver(CnasDbContext db, IAuditService? audit = null)
    {
        return new(db, NewSqidMock(), audit ?? Substitute.For<IAuditService>());
    }

    /// <summary>Seeds a user-profile and returns its id.</summary>
    private static async Task<long> SeedUserAsync(CnasDbContext db)
    {
        var user = new UserProfile { DisplayName = "T", CreatedAtUtc = ClockNow, IsActive = true };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Seeds a user-group and returns it.</summary>
    private static async Task<UserGroup> SeedGroupAsync(
        CnasDbContext db,
        string code,
        UserGroupStatus status = UserGroupStatus.Active,
        params string[] roles)
    {
        var grp = new UserGroup
        {
            Code = code,
            DisplayName = code,
            Kind = UserGroupKind.OrganizationalUnit,
            Status = status,
            Roles = roles.ToList(),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.UserGroups.Add(grp);
        await db.SaveChangesAsync();
        return grp;
    }

    /// <summary>R2270 — direct membership in a single group returns its roles.</summary>
    [Fact]
    public async Task ResolveEffectiveRolesAsync_SingleDirectGroup_ReturnsRoles()
    {
        var db = CreateContext();
        var sut = NewResolver(db);
        var grp = await SeedGroupAsync(db, "DIRECT", UserGroupStatus.Active, "ROLE_A", "ROLE_B");
        var userId = await SeedUserAsync(db);
        db.UserGroupMemberships.Add(new UserGroupMembership
        {
            UserGroupId = grp.Id,
            UserProfileId = userId,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await sut.ResolveEffectiveRolesAsync(userId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Roles.Select(r => r.RoleCode).Should().BeEquivalentTo(["ROLE_A", "ROLE_B"]);
    }

    /// <summary>R2270 — nested chain A → B → C resolves to all three groups' roles.</summary>
    [Fact]
    public async Task ResolveEffectiveRolesAsync_NestedChain_AggregatesAllAncestors()
    {
        // C is the user's direct group; B is C's parent; A is B's parent.
        var db = CreateContext();
        var sut = NewResolver(db);
        var a = await SeedGroupAsync(db, "A", UserGroupStatus.Active, "ROLE_A");
        var b = await SeedGroupAsync(db, "B", UserGroupStatus.Active, "ROLE_B");
        var c = await SeedGroupAsync(db, "C", UserGroupStatus.Active, "ROLE_C");
        // Nesting: A is parent of B, B is parent of C.
        db.UserGroupParents.AddRange(
            new UserGroupParent { ParentGroupId = a.Id, ChildGroupId = b.Id, CreatedAtUtc = ClockNow, IsActive = true },
            new UserGroupParent { ParentGroupId = b.Id, ChildGroupId = c.Id, CreatedAtUtc = ClockNow, IsActive = true });
        var userId = await SeedUserAsync(db);
        db.UserGroupMemberships.Add(new UserGroupMembership
        {
            UserGroupId = c.Id,
            UserProfileId = userId,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await sut.ResolveEffectiveRolesAsync(userId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Roles.Select(r => r.RoleCode).Should().BeEquivalentTo(["ROLE_A", "ROLE_B", "ROLE_C"]);
    }

    /// <summary>R2270 — disabled ancestor's roles are excluded from the resolution.</summary>
    [Fact]
    public async Task ResolveEffectiveRolesAsync_DisabledAncestor_RolesExcluded()
    {
        var db = CreateContext();
        var sut = NewResolver(db);
        var a = await SeedGroupAsync(db, "A_DIS", UserGroupStatus.Disabled, "ROLE_A_DIS");
        var b = await SeedGroupAsync(db, "B_ACT", UserGroupStatus.Active, "ROLE_B");
        db.UserGroupParents.Add(new UserGroupParent
        {
            ParentGroupId = a.Id,
            ChildGroupId = b.Id,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        var userId = await SeedUserAsync(db);
        db.UserGroupMemberships.Add(new UserGroupMembership
        {
            UserGroupId = b.Id,
            UserProfileId = userId,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await sut.ResolveEffectiveRolesAsync(userId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Roles.Select(r => r.RoleCode).Should().BeEquivalentTo(["ROLE_B"]);
    }

    /// <summary>R2270 — user with no memberships → empty result, success.</summary>
    [Fact]
    public async Task ResolveEffectiveRolesAsync_NoMemberships_ReturnsEmpty()
    {
        var db = CreateContext();
        var sut = NewResolver(db);
        var userId = await SeedUserAsync(db);

        var result = await sut.ResolveEffectiveRolesAsync(userId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Roles.Should().BeEmpty();
    }
}
