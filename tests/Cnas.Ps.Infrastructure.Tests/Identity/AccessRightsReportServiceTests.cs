using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Identity;

/// <summary>
/// R2274 / TOR SEC 028 — service-level tests for
/// <see cref="AccessRightsReportService"/>. Exercises the three projections,
/// disabled-account / disabled-group exclusion, and CSV quoting.
/// </summary>
public sealed class AccessRightsReportServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-access-rights-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub round-tripping "USR-{id}" / "GRP-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"USR-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && (s.StartsWith("USR-", StringComparison.Ordinal) || s.StartsWith("GRP-", StringComparison.Ordinal))
                && long.TryParse(s[4..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Authenticated caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(99L);
        caller.UserSqid.Returns("USR-99");
        caller.SourceIp.Returns("203.0.113.99");
        caller.CorrelationId.Returns("corr-arr");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT with a captured audit list.</summary>
    private static (AccessRightsReportService Sut, List<(string Code, AuditSeverity Severity)> AuditCalls)
        NewService(CnasDbContext db)
    {
        var calls = new List<(string Code, AuditSeverity Severity)>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add((call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1)));
                return Task.FromResult(Result.Success());
            });

        var resolver = new UserGroupRoleResolver(db, NewSqidMock(), audit);
        var sut = new AccessRightsReportService(
            db,
            resolver,
            NewSqidMock(),
            new StubClock(ClockNow),
            NewCaller(),
            audit);
        return (sut, calls);
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
            DisplayName = $"DN-{code}",
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

    /// <summary>Seeds a user with the supplied direct roles and returns its id.</summary>
    private static async Task<UserProfile> SeedUserAsync(
        CnasDbContext db,
        string displayName,
        string? email = null,
        UserAccountState state = UserAccountState.Active,
        params string[] roles)
    {
        var user = new UserProfile
        {
            DisplayName = displayName,
            Email = email,
            State = state,
            Roles = roles.ToList(),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Adds a direct membership row linking <paramref name="user"/> to <paramref name="group"/>.</summary>
    private static async Task LinkAsync(CnasDbContext db, UserProfile user, UserGroup group)
    {
        db.UserGroupMemberships.Add(new UserGroupMembership
        {
            UserGroupId = group.Id,
            UserProfileId = user.Id,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds a parent-child nesting row.</summary>
    private static async Task NestAsync(CnasDbContext db, UserGroup parent, UserGroup child)
    {
        db.UserGroupParents.Add(new UserGroupParent
        {
            ParentGroupId = parent.Id,
            ChildGroupId = child.Id,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    // ─── ReportByUserAsync ───

    /// <summary>R2274 — unknown user returns NOT_FOUND.</summary>
    [Fact]
    public async Task ReportByUserAsync_NonExistentUser_ReturnsNotFound()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);

        var result = await sut.ReportByUserAsync("USR-99999");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>R2274 — user with only direct roles → all roles tagged Direct.</summary>
    [Fact]
    public async Task ReportByUserAsync_DirectRoleOnly_ReturnsDirectGrantKind()
    {
        var db = CreateContext();
        var (sut, audit) = NewService(db);
        var user = await SeedUserAsync(db, "Ion Direct", "ion@example.com", UserAccountState.Active, "ROLE_DIRECT");

        var result = await sut.ReportByUserAsync($"USR-{user.Id}");

        result.IsSuccess.Should().BeTrue();
        result.Value.DirectRoles.Should().BeEquivalentTo(["ROLE_DIRECT"]);
        result.Value.EffectiveRoles.Should().ContainSingle();
        result.Value.EffectiveRoles[0].RoleCode.Should().Be("ROLE_DIRECT");
        result.Value.EffectiveRoles[0].GrantKind.Should().Be(nameof(AccessRightsGrantKind.Direct));
        result.Value.EffectiveRoles[0].GrantingGroupChain.Should().BeEmpty();
        result.Value.AccountStatus.Should().Be(nameof(UserAccountState.Active));
        audit.Should().Contain(a => a.Code == AccessRightsReportService.AuditReportGenerated);
    }

    /// <summary>R2274 — chain A → B; user is member of B → ROLE_A is inherited with chain [B, A].</summary>
    [Fact]
    public async Task ReportByUserAsync_InheritedRoleViaGroupChain_ReturnsInheritedWithChain()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var a = await SeedGroupAsync(db, "A", UserGroupStatus.Active, "ROLE_A");
        var b = await SeedGroupAsync(db, "B", UserGroupStatus.Active, "ROLE_B");
        await NestAsync(db, parent: a, child: b);
        var user = await SeedUserAsync(db, "Maria");
        await LinkAsync(db, user, b);

        var result = await sut.ReportByUserAsync($"USR-{user.Id}");

        result.IsSuccess.Should().BeTrue();
        var inheritedA = result.Value.EffectiveRoles.SingleOrDefault(r => r.RoleCode == "ROLE_A");
        inheritedA.Should().NotBeNull();
        inheritedA!.GrantKind.Should().Be(nameof(AccessRightsGrantKind.Inherited));
        inheritedA.GrantingGroupChain.Should().BeEquivalentTo(["B", "A"], opts => opts.WithStrictOrdering());

        var inheritedB = result.Value.EffectiveRoles.SingleOrDefault(r => r.RoleCode == "ROLE_B");
        inheritedB.Should().NotBeNull();
        inheritedB!.GrantKind.Should().Be(nameof(AccessRightsGrantKind.Inherited));

        // Group memberships projected.
        result.Value.GroupMemberships.Should().ContainSingle(gm => gm.GroupCode == "B");
    }

    // ─── ReportByRoleAsync ───

    /// <summary>R2274 — user with the role on their UserProfile shows up as Direct.</summary>
    [Fact]
    public async Task ReportByRoleAsync_DirectGrant_ReturnsUser()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var user = await SeedUserAsync(db, "Direct User", "du@example.com", UserAccountState.Active, "ROLE_DIRECT");

        var result = await sut.ReportByRoleAsync(
            "ROLE_DIRECT",
            new AccessRightsReportPagingDto(0, 100, IncludeDisabledAccounts: false));

        result.IsSuccess.Should().BeTrue();
        result.Value.RoleCode.Should().Be("ROLE_DIRECT");
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].UserSqid.Should().Be($"USR-{user.Id}");
        result.Value.Items[0].GrantKind.Should().Be(nameof(AccessRightsGrantKind.Direct));
        result.Value.Items[0].GrantingGroups.Should().BeEmpty();
    }

    /// <summary>R2274 — A → B nesting; user in B; query for ROLE_A returns the user as Inherited.</summary>
    [Fact]
    public async Task ReportByRoleAsync_TransitiveGrantViaAncestorGroup_ReturnsUser()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var a = await SeedGroupAsync(db, "GRP_A", UserGroupStatus.Active, "ROLE_HQ");
        var b = await SeedGroupAsync(db, "GRP_B", UserGroupStatus.Active);
        await NestAsync(db, parent: a, child: b);
        var user = await SeedUserAsync(db, "Member");
        await LinkAsync(db, user, b);

        var result = await sut.ReportByRoleAsync(
            "ROLE_HQ",
            new AccessRightsReportPagingDto(0, 100, IncludeDisabledAccounts: false));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        var row = result.Value.Items[0];
        row.UserSqid.Should().Be($"USR-{user.Id}");
        row.GrantKind.Should().Be(nameof(AccessRightsGrantKind.Inherited));
        row.GrantingGroups.Should().Contain("GRP_A");
    }

    // ─── ReportByGroupAsync ───

    /// <summary>R2274 — group with descendants aggregates roles from active descendants.</summary>
    [Fact]
    public async Task ReportByGroupAsync_AggregatesRolesFromDescendantSubtree()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var root = await SeedGroupAsync(db, "ROOT", UserGroupStatus.Active, "ROLE_ROOT");
        var child = await SeedGroupAsync(db, "CHILD", UserGroupStatus.Active, "ROLE_CHILD");
        await NestAsync(db, parent: root, child: child);
        var u1 = await SeedUserAsync(db, "Root User");
        var u2 = await SeedUserAsync(db, "Child User");
        await LinkAsync(db, u1, root);
        await LinkAsync(db, u2, child);

        var result = await sut.ReportByGroupAsync($"USR-{root.Id}");

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupCode.Should().Be("ROOT");
        result.Value.AggregatedRoleCodes.Should().BeEquivalentTo(["ROLE_ROOT", "ROLE_CHILD"]);
        result.Value.DescendantGroupCodes.Should().Contain("CHILD");

        // Both users are reachable through the subtree.
        result.Value.Members.Should().HaveCount(2);
        result.Value.Members.Should()
            .Contain(m => m.SourceGroupCode == "ROOT" && m.GrantKind == nameof(AccessRightsGrantKind.DirectInGroup));
        result.Value.Members.Should()
            .Contain(m => m.SourceGroupCode == "CHILD" && m.GrantKind == nameof(AccessRightsGrantKind.InheritedFromDescendant));
    }

    /// <summary>R2274 — disabled descendant's roles are NOT included in the aggregated set.</summary>
    [Fact]
    public async Task ReportByGroupAsync_DisabledDescendantExcluded()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var root = await SeedGroupAsync(db, "ROOT2", UserGroupStatus.Active, "ROLE_ROOT");
        var disabledChild = await SeedGroupAsync(db, "DIS", UserGroupStatus.Disabled, "ROLE_DISABLED");
        await NestAsync(db, parent: root, child: disabledChild);

        var result = await sut.ReportByGroupAsync($"USR-{root.Id}");

        result.IsSuccess.Should().BeTrue();
        result.Value.AggregatedRoleCodes.Should().BeEquivalentTo(["ROLE_ROOT"]);
    }

    // ─── ExportByRoleCsvAsync ───

    /// <summary>R2274 — DisplayName with commas/newlines is RFC 4180-quoted in the CSV.</summary>
    [Fact]
    public async Task ExportByRoleCsvAsync_QuotesFieldsWithCommasAndNewlines()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        var user = await SeedUserAsync(db, "Ion, Popescu\nJr.", "ip@example.com", UserAccountState.Active, "ROLE_FANCY");

        var result = await sut.ExportByRoleCsvAsync("ROLE_FANCY");

        result.IsSuccess.Should().BeTrue();
        var csv = Encoding.UTF8.GetString(result.Value);
        // Header
        csv.Should().StartWith("UserSqid,DisplayName,Email,AccountStatus,DirectGrant,GrantingGroups\r\n");
        // RFC 4180 quoting — display name must be wrapped in quotes; comma/newline preserved inside.
        csv.Should().Contain("\"Ion, Popescu\nJr.\"");
        // No BOM
        result.Value.Length.Should().BeGreaterThan(0);
        result.Value[0].Should().NotBe(0xEF);
    }

    /// <summary>R2274 — full-matrix export honours paging cap so payload stays bounded.</summary>
    [Fact]
    public async Task ExportFullAccessMatrixCsvAsync_RespectsPaging()
    {
        var db = CreateContext();
        var (sut, _) = NewService(db);
        for (int i = 0; i < 5; i++)
        {
            await SeedUserAsync(db, $"User{i}", $"u{i}@example.com", UserAccountState.Active, "ROLE_X");
        }

        var result = await sut.ExportFullAccessMatrixCsvAsync(
            new AccessRightsReportPagingDto(Skip: 0, Take: 2, IncludeDisabledAccounts: false));

        result.IsSuccess.Should().BeTrue();
        var csv = Encoding.UTF8.GetString(result.Value);
        // Header + 2 data rows = 3 lines.
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(3);
        lines[0].Should().StartWith("UserSqid,DisplayName,Email,AccountStatus,RoleCode,GrantKind,GrantingChain");
    }
}
