using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="UserAdministrationService"/>. Uses EF Core InMemory +
/// NSubstitute, following the same pattern as <see cref="ContributorServiceTests"/>.
/// Exercises every <see cref="Result"/> branch in the four mutating operations plus the
/// paged <see cref="UserAdministrationService.ListAsync"/> read.
/// </summary>
public class UserAdministrationServiceTests
{
    /// <summary>Deterministic clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Caller role-set that satisfies the admin guard.</summary>
    private static readonly string[] AdminRoles = ["cnas-admin"];

    /// <summary>Caller role-set without admin rights — used to verify the 403 path.</summary>
    private static readonly string[] NonAdminRoles = ["cnas-user"];

    // ─────────────────────── GrantRoleAsync ───────────────────────

    [Fact]
    public async Task GrantRoleAsync_CallerLacksAdminRole_ReturnsForbidden()
    {
        var harness = Harness.Create(roles: NonAdminRoles);
        var user = await harness.SeedUserAsync();
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.GrantRoleAsync("UID", "cnas-decider");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task GrantRoleAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.GrantRoleAsync("bad", "cnas-decider");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task GrantRoleAsync_UserNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.GrantRoleAsync("missing", "cnas-decider");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GrantRoleAsync_RoleAlreadyGranted_NoChange_StillReturnsSuccess()
    {
        // Idempotency contract — re-granting an existing role must not duplicate or fail.
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(roles: ["cnas-decider"]);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.GrantRoleAsync("UID", "cnas-decider");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.Roles.Should().BeEquivalentTo(["cnas-decider"]);
    }

    [Fact]
    public async Task GrantRoleAsync_HappyPath_AppendsRoleAndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(roles: ["cnas-user"]);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.GrantRoleAsync("UID", "cnas-admin");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.Roles.Should().BeEquivalentTo(["cnas-user", "cnas-admin"]);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "USER.ROLE_GRANTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Is<string>(s => s.Contains("\"role\":\"cnas-admin\"")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── RevokeRoleAsync ───────────────────────

    [Fact]
    public async Task RevokeRoleAsync_HappyPath_RemovesRoleAndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(roles: ["cnas-user", "cnas-decider"]);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.RevokeRoleAsync("UID", "cnas-decider");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.Roles.Should().BeEquivalentTo(["cnas-user"]);

        await harness.Audit.Received(1).RecordAsync(
            "USER.ROLE_REVOKED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Is<string>(s => s.Contains("\"role\":\"cnas-decider\"")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeRoleAsync_RoleNotPresent_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(roles: ["cnas-user"]);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.RevokeRoleAsync("UID", "cnas-decider");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.Roles.Should().BeEquivalentTo(["cnas-user"]);
    }

    // ─────────────────────── Lock / Unlock ───────────────────────

    [Fact]
    public async Task LockAsync_HappyPath_SetsFlagAndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync();
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.LockAsync("UID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        // R0059 — Lock translates to a State.Active → State.Locked transition.
        reloaded.State.Should().Be(UserAccountState.Locked);

        await harness.Audit.Received(1).RecordAsync(
            "USER.LOCKED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockAsync_HappyPath_ClearsFlagAndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: UserAccountState.Locked);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.UnlockAsync("UID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        // R0059 — Unlock translates to a State.Locked → State.Active transition.
        reloaded.State.Should().Be(UserAccountState.Active);

        await harness.Audit.Received(1).RecordAsync(
            "USER.UNLOCKED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── DeactivateAsync (R0672 / TOR CF 18.08) ───────────────────────

    /// <summary>
    /// R0672 regression — the service MUST consult
    /// <see cref="Cnas.Ps.Application.Users.IUserDeactivationGuard"/> before
    /// flipping <see cref="UserProfile.IsActive"/>. A guard refusal must
    /// surface verbatim and the row MUST stay active.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_GuardRefuses_DoesNotFlipIsActive()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync();
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        // Re-stub the guard to refuse.
        harness.DeactivationGuard
            .EnsureCanDeactivateAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure(
                ErrorCodes.UserProfileNoAuditHistory,
                "no trail rows yet")));

        var result = await harness.Service.DeactivateAsync("UID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserProfileNoAuditHistory);

        // Reload and assert the IsActive flag was NOT mutated.
        var reloaded = await harness.Db.UserProfiles.AsNoTracking()
            .SingleAsync(u => u.Id == user.Id);
        reloaded.IsActive.Should().BeTrue(
            "the guard refusal must short-circuit BEFORE the IsActive flip lands.");

        await harness.DeactivationGuard.Received(1)
            .EnsureCanDeactivateAsync(user.Id, Arg.Any<CancellationToken>());

        // No audit row should have been written because no state change happened.
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task ListAsync_PagedResults_ReturnsSqidEncodedIds()
    {
        var harness = Harness.Create();
        await harness.SeedUserAsync(displayName: "Alpha");
        await harness.SeedUserAsync(displayName: "Beta");

        var result = await harness.Service.ListAsync(new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().HaveCount(2);
        // Sqid encoder used by the harness prefixes the long id with "SQID-".
        result.Value.Items.Should().AllSatisfy(item => item.Id.Should().StartWith("SQID-"));
        result.Value.Items.Select(i => i.DisplayName).Should().BeEquivalentTo(["Alpha", "Beta"]);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-useradm-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UserAdministrationService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }
        public required Cnas.Ps.Application.Users.IUserDeactivationGuard DeactivationGuard { get; init; }

        public static Harness Create(string[]? roles = null)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(roles ?? AdminRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            // R0672 — deactivation guard substitute defaults to success so the
            // pre-existing tests that don't care about the audit-history
            // policy continue to pass; the dedicated deactivation tests
            // re-stub it as needed.
            var guard = Substitute.For<Cnas.Ps.Application.Users.IUserDeactivationGuard>();
            guard.EnsureCanDeactivateAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new UserAdministrationService(db, sqids, clock, caller, audit, guard);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Sqids = sqids,
                DeactivationGuard = guard,
            };
        }

        public async Task<UserProfile> SeedUserAsync(
            string displayName = "Default User",
            List<string>? roles = null,
            UserAccountState state = UserAccountState.Active)
        {
            var entity = new UserProfile
            {
                MPassSubject = $"sub-{Guid.NewGuid():N}",
                DisplayName = displayName,
                Email = $"{displayName.Replace(' ', '.').ToLowerInvariant()}@example.md",
                Roles = roles ?? [],
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
                State = state,
            };
            Db.UserProfiles.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
