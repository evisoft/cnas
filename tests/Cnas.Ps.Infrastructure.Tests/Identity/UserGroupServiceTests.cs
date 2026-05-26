using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
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
/// R2270 / TOR SEC 023-024 — service-level tests for <see cref="UserGroupService"/>.
/// Exercises create / modify / cycle prevention / member management lifecycle.
/// </summary>
public sealed class UserGroupServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-user-groups-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
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

    /// <summary>Captures audit invocations for assertion.</summary>
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity, long? TargetId)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity, long? TargetId)>();
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
                calls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<long?>(4)));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => calls);
    }

    /// <summary>Authenticated caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-grp");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static UserGroupService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            new UserGroupCreateInputValidator(),
            new UserGroupModifyInputValidator(),
            new UserGroupReasonInputValidator());
    }

    /// <summary>Builds a canonical create-input.</summary>
    private static UserGroupCreateInputDto BuildCreateInput(
        string code = "OFFICE_CHISINAU_CENTRU",
        params string[] roles)
    {
        return new(
            Code: code,
            DisplayName: $"Group {code}",
            Description: null,
            Kind: nameof(UserGroupKind.OrganizationalUnit),
            Roles: roles.Length == 0 ? Array.Empty<string>() : roles);
    }

    /// <summary>Seeds a user-profile row and returns its id.</summary>
    private static async Task<long> SeedUserAsync(CnasDbContext db, string displayName = "Test User")
    {
        var user = new UserProfile
        {
            DisplayName = displayName,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ───────── CreateAsync ─────────

    /// <summary>R2270 — create happy path persists Active + emits Critical audit + counter.</summary>
    [Fact]
    public async Task CreateAsync_HappyPath_PersistsAndAudits()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(BuildCreateInput("OFFICE_A", "USER_CNAS"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(UserGroupStatus.Active));
        result.Value.Code.Should().Be("OFFICE_A");
        result.Value.Roles.Should().Contain("USER_CNAS");
        (await db.UserGroups.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == UserGroupService.AuditCreated
            && c.Severity == AuditSeverity.Critical);
    }

    /// <summary>R2270 — duplicate code → Conflict, no second row written.</summary>
    [Fact]
    public async Task CreateAsync_DuplicateCode_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.CreateAsync(BuildCreateInput("DUP_CODE"));
        var second = await sut.CreateAsync(BuildCreateInput("DUP_CODE"));

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        (await db.UserGroups.CountAsync()).Should().Be(1);
    }

    // ───────── ModifyAsync ─────────

    /// <summary>R2270 — modify after delete → NotFound (the soft-delete filter hides the row).</summary>
    [Fact]
    public async Task ModifyAsync_AfterDelete_ReturnsNotFound()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput("DELETED_GRP"));

        var deleted = await sut.DeleteAsync(created.Value.Id, new UserGroupReasonInputDto("not needed any more"));
        deleted.IsSuccess.Should().BeTrue();

        var modify = await sut.ModifyAsync(
            created.Value.Id,
            new UserGroupModifyInputDto(
                DisplayName: "rename",
                Description: null,
                Kind: null,
                Roles: null,
                ChangeReason: "rename reason"));

        modify.IsFailure.Should().BeTrue();
        modify.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ───────── AddChildAsync — cycle prevention ─────────

    /// <summary>R2270 — self-loop add-child → Conflict + cycle counter.</summary>
    [Fact]
    public async Task AddChildAsync_SelfLoop_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var grp = await sut.CreateAsync(BuildCreateInput("SELFLOOP"));

        var result = await sut.AddChildAsync(grp.Value.Id, grp.Value.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>R2270 — transitive cycle attempt → Conflict.</summary>
    [Fact]
    public async Task AddChildAsync_TransitiveCycle_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var a = await sut.CreateAsync(BuildCreateInput("GRP_A"));
        var b = await sut.CreateAsync(BuildCreateInput("GRP_B"));
        var c = await sut.CreateAsync(BuildCreateInput("GRP_C"));

        // Build chain: A -> B -> C  (A is parent of B; B is parent of C)
        await sut.AddChildAsync(a.Value.Id, b.Value.Id);
        await sut.AddChildAsync(b.Value.Id, c.Value.Id);

        // Now try to make C a parent of A — that would close the cycle A -> B -> C -> A
        var cycle = await sut.AddChildAsync(c.Value.Id, a.Value.Id);

        cycle.IsFailure.Should().BeTrue();
        cycle.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>R2270 — add-child happy path persists the join row + emits audit.</summary>
    [Fact]
    public async Task AddChildAsync_HappyPath_PersistsJoinRow()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var parent = await sut.CreateAsync(BuildCreateInput("PARENT_GRP", "ADMIN_ROLE"));
        var child = await sut.CreateAsync(BuildCreateInput("CHILD_GRP"));

        var result = await sut.AddChildAsync(parent.Value.Id, child.Value.Id);

        result.IsSuccess.Should().BeTrue();
        (await db.UserGroupParents.CountAsync()).Should().Be(1);
        calls().Should().Contain(c =>
            c.Code == UserGroupService.AuditChildAdded
            && c.Severity == AuditSeverity.Critical);
    }

    // ───────── AddMember / RemoveMember ─────────

    /// <summary>R2270 — add/remove member round trip.</summary>
    [Fact]
    public async Task MemberLifecycle_AddThenRemove_PersistsAndAudits()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var grp = await sut.CreateAsync(BuildCreateInput("MEMB_GRP"));
        var userId = await SeedUserAsync(db);

        var added = await sut.AddMemberAsync(grp.Value.Id, $"USR-{userId}");
        added.IsSuccess.Should().BeTrue();
        (await db.UserGroupMemberships.CountAsync()).Should().Be(1);
        added.Value.DirectMemberCount.Should().Be(1);

        var removed = await sut.RemoveMemberAsync(grp.Value.Id, $"USR-{userId}");
        removed.IsSuccess.Should().BeTrue();
        (await db.UserGroupMemberships.Where(m => m.IsActive).CountAsync()).Should().Be(0);

        calls().Should().Contain(c => c.Code == UserGroupService.AuditMemberAdded);
        calls().Should().Contain(c => c.Code == UserGroupService.AuditMemberRemoved);
    }
}
