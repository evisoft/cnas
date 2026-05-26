using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Service-layer tests for <see cref="UserAccountStateService"/> (R0059 / SEC 016). Uses
/// EF Core InMemory + NSubstitute, mirroring the pattern in
/// <see cref="UserAdministrationServiceTests"/>. Exercises every branch of the transition
/// matrix plus the auto-lock convenience path.
/// </summary>
public class UserAccountStateServiceTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── ChangeStateAsync ───────────────────────

    [Fact]
    public async Task ChangeStateAsync_UnknownUser_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("MISSING").Returns(Result<long>.Success(99_999L));

        var result = await harness.Service.ChangeStateAsync(
            "MISSING", UserAccountState.Suspended, reason: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    /// <summary>
    /// Each row is a disallowed (from, to) pair the matrix MUST reject. Includes a
    /// no-op same-state transition because <c>Active → Active</c> is not in the allow
    /// list and the service must refuse it explicitly.
    /// </summary>
    public static TheoryData<UserAccountState, UserAccountState> DisallowedTransitions => new()
    {
        { UserAccountState.Disabled, UserAccountState.Suspended }, // Disabled cannot become Suspended
        { UserAccountState.Disabled, UserAccountState.Locked },    // Disabled cannot become Locked
        { UserAccountState.Suspended, UserAccountState.Locked },   // Suspended cannot become Locked
        { UserAccountState.Locked, UserAccountState.Suspended },   // Locked cannot become Suspended
        { UserAccountState.Active, UserAccountState.Active },      // No-op same-state denied
    };

    [Theory]
    [MemberData(nameof(DisallowedTransitions))]
    public async Task ChangeStateAsync_DisallowedTransition_ReturnsTransitionForbidden(
        UserAccountState from, UserAccountState to)
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: from);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.ChangeStateAsync("UID", to, reason: "test");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserAccountStateTransitionForbidden);
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.State.Should().Be(from, "the state must not flip on a rejected transition");
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    /// <summary>Each row is an allowed transition the matrix MUST accept.</summary>
    public static TheoryData<UserAccountState, UserAccountState> AllowedTransitions => new()
    {
        { UserAccountState.Active, UserAccountState.Suspended },
        { UserAccountState.Active, UserAccountState.Disabled },
        { UserAccountState.Active, UserAccountState.Locked },
        { UserAccountState.Suspended, UserAccountState.Active },
        { UserAccountState.Locked, UserAccountState.Active },
        { UserAccountState.Disabled, UserAccountState.Active },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public async Task ChangeStateAsync_AllowedTransition_FlipsStateAndWritesAudit(
        UserAccountState from, UserAccountState to)
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: from);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.ChangeStateAsync("UID", to, reason: "test");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.State.Should().Be(to);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        // Audit row: stable event code, critical severity, target id is the raw user
        // primary key. Payload carries the (from, to, reason) trio but NEVER any PII —
        // in particular, the email and IDNP fields must be absent from the json.
        var expectedEventCode = $"USER.STATE_CHANGE.{from}.{to}";
        await harness.Audit.Received(1).RecordAsync(
            expectedEventCode,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Is<string>(s =>
                s.Contains($"\"from\":\"{from}\"") &&
                s.Contains($"\"to\":\"{to}\"") &&
                !s.Contains(user.Email!, StringComparison.OrdinalIgnoreCase) &&
                !s.Contains("0000000000000", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeStateAsync_CallerLacksAdminRole_ReturnsForbidden()
    {
        var harness = Harness.Create(roles: ["cnas-user"]);
        var user = await harness.SeedUserAsync(state: UserAccountState.Active);
        harness.Sqids.TryDecode("UID").Returns(Result<long>.Success(user.Id));

        var result = await harness.Service.ChangeStateAsync(
            "UID", UserAccountState.Suspended, reason: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task ChangeStateAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(
            Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.ChangeStateAsync(
            "bad", UserAccountState.Suspended, reason: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    // ─────────────────────── LockForFailedLoginsAsync ───────────────────────

    [Fact]
    public async Task LockForFailedLoginsAsync_ActiveUser_FlipsToLocked()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: UserAccountState.Active);

        var result = await harness.Service.LockForFailedLoginsAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.State.Should().Be(UserAccountState.Locked);

        // Auto-locks attribute the actor to "system" (no human admin in the loop).
        await harness.Audit.Received(1).RecordAsync(
            "USER.STATE_CHANGE.Active.Locked",
            AuditSeverity.Critical,
            "system",
            nameof(UserProfile),
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LockForFailedLoginsAsync_AlreadyLocked_IsIdempotent()
    {
        // Arrange — user is already locked; a second auto-lock call must succeed without
        // writing a redundant audit row.
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: UserAccountState.Locked);

        var result = await harness.Service.LockForFailedLoginsAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.State.Should().Be(UserAccountState.Locked);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task LockForFailedLoginsAsync_DisabledUser_ReturnsTransitionForbidden()
    {
        // Disabled accounts cannot be auto-locked — the disabled state already rejects
        // sign-in. The auto-lock pipeline is expected to no-op via this failure code.
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(state: UserAccountState.Disabled);

        var result = await harness.Service.LockForFailedLoginsAsync(user.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserAccountStateTransitionForbidden);
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.State.Should().Be(UserAccountState.Disabled);
    }

    // ─────────────────────── BulkSuspend / BulkUnlock ───────────────────────

    [Fact]
    public async Task BulkSuspendAsync_AllActive_FlipsAllAndReportsZeroFailures()
    {
        var harness = Harness.Create();
        var u1 = await harness.SeedUserAsync(state: UserAccountState.Active);
        var u2 = await harness.SeedUserAsync(state: UserAccountState.Active);
        var u3 = await harness.SeedUserAsync(state: UserAccountState.Active);
        harness.Sqids.TryDecode("U1").Returns(Result<long>.Success(u1.Id));
        harness.Sqids.TryDecode("U2").Returns(Result<long>.Success(u2.Id));
        harness.Sqids.TryDecode("U3").Returns(Result<long>.Success(u3.Id));

        var result = await harness.Service.BulkSuspendAsync(
            ["U1", "U2", "U3"], reason: "compromised credentials");

        result.IsSuccess.Should().BeTrue();
        result.Value!.TotalRequested.Should().Be(3);
        result.Value.Succeeded.Should().Be(3);
        result.Value.Failed.Should().Be(0);
        result.Value.Failures.Should().BeEmpty();

        var ids = new[] { u1.Id, u2.Id, u3.Id };
        var reloaded = await harness.Db.UserProfiles.Where(u => ids.Contains(u.Id)).ToListAsync();
        reloaded.Should().OnlyContain(u => u.State == UserAccountState.Suspended);
    }

    [Fact]
    public async Task BulkSuspendAsync_AlreadySuspended_ReportsFailureForThoseRows()
    {
        var harness = Harness.Create();
        var active = await harness.SeedUserAsync(state: UserAccountState.Active);
        var already = await harness.SeedUserAsync(state: UserAccountState.Suspended);
        harness.Sqids.TryDecode("U_ACTIVE").Returns(Result<long>.Success(active.Id));
        harness.Sqids.TryDecode("U_SUSP").Returns(Result<long>.Success(already.Id));

        var result = await harness.Service.BulkSuspendAsync(
            ["U_ACTIVE", "U_SUSP"], reason: "test");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().Be(1);
        result.Value.Failed.Should().Be(1);
        result.Value.Failures.Should().ContainSingle()
            .Which.UserSqid.Should().Be("U_SUSP");
    }

    [Fact]
    public async Task BulkUnlockAsync_AllLocked_FlipsAllToActive()
    {
        var harness = Harness.Create();
        var u1 = await harness.SeedUserAsync(state: UserAccountState.Locked);
        var u2 = await harness.SeedUserAsync(state: UserAccountState.Locked);
        harness.Sqids.TryDecode("L1").Returns(Result<long>.Success(u1.Id));
        harness.Sqids.TryDecode("L2").Returns(Result<long>.Success(u2.Id));

        var result = await harness.Service.BulkUnlockAsync(
            ["L1", "L2"], reason: "investigation cleared");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().Be(2);
        var ids = new[] { u1.Id, u2.Id };
        var reloaded = await harness.Db.UserProfiles.Where(u => ids.Contains(u.Id)).ToListAsync();
        reloaded.Should().OnlyContain(u => u.State == UserAccountState.Active);
    }

    [Fact]
    public async Task BulkSuspendAsync_NotAdmin_ReturnsForbidden()
    {
        var harness = Harness.Create(roles: ["cnas-user"]);

        var result = await harness.Service.BulkSuspendAsync(
            ["U1"], reason: "test");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-accstate-{Guid.NewGuid():N}")
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
        public required UserAccountStateService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }

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
            caller.Roles.Returns(roles ?? ["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var service = new UserAccountStateService(db, sqids, clock, caller, audit);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Sqids = sqids,
            };
        }

        public async Task<UserProfile> SeedUserAsync(UserAccountState state)
        {
            var entity = new UserProfile
            {
                MPassSubject = $"sub-{Guid.NewGuid():N}",
                DisplayName = "Test User",
                Email = "test.user@example.md",
                Roles = ["cnas-user"],
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
