using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="LocalLoginService"/> — the R0051 local-credential entry
/// point. Per CLAUDE.md RULE 1 these tests are written before the service code
/// (Red → Green → Refactor) and cover every published failure mode plus the happy
/// path. Account-enumeration prevention is asserted by checking that the wire
/// error code is the SAME for unknown-login, wrong-password, and wrong-role; the
/// audit event codes differentiate internally.
/// </summary>
public sealed class LocalLoginServiceTests
{
    /// <summary>Deterministic clock instant.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LoginAsync_UnknownLogin_ReturnsLoginInvalid_AndAudits()
    {
        var harness = Harness.Create();
        // No user seeded — login lookup misses.

        var input = new LocalLoginInputDto("unknown.user", "Aa1!aaaa");
        var result = await harness.Service.LoginAsync(input, "127.0.0.1", "test-agent");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);

        // Audit row written with the unknown-login event code (not the generic
        // LOGIN.INVALID — internal forensics retain the precise outcome).
        await harness.Audit.Received().RecordAsync(
            "USER.LOGIN.UNKNOWN",
            AuditSeverity.Notice,
            "anonymous",
            nameof(UserProfile),
            null,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ReturnsLoginInvalid_AndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(localLogin: "alice", password: "Aa1!aaaa",
            roles: ["utilizator-autorizat"]);

        var input = new LocalLoginInputDto("alice", "WrongPass99!");
        var result = await harness.Service.LoginAsync(input, "10.0.0.1", "agent");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);

        // Bad-password audit row carries the user's sqid as actor + the consecutive
        // failure count in the JSON payload.
        await harness.Audit.Received().RecordAsync(
            "USER.LOGIN.BAD_PASSWORD",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Is<string>(s => s.Contains("consecutiveFailures", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Failure tracker incremented.
        harness.FailureTracker.GetFailureCount(user.Id).Should().Be(1);
    }

    [Fact]
    public async Task LoginAsync_UnknownLogin_AndWrongPassword_ReturnSameErrorCode()
    {
        // Account-enumeration prevention — the wire response is identical for
        // "user does not exist" and "user exists but password is wrong".
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(localLogin: "alice", password: "Aa1!aaaa",
            roles: ["utilizator-autorizat"]);

        var unknownResult = await harness.Service.LoginAsync(
            new LocalLoginInputDto("ghost.user", "Anything9!"), null, null);
        var badPasswordResult = await harness.Service.LoginAsync(
            new LocalLoginInputDto("alice", "ZzzzZ9z!"), null, null);

        unknownResult.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);
        badPasswordResult.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);
        unknownResult.ErrorMessage.Should().Be(badPasswordResult.ErrorMessage);
    }

    [Fact]
    public async Task LoginAsync_WrongRole_ReturnsLoginInvalid_AndAudits()
    {
        var harness = Harness.Create();
        // User lacks the utilizator-autorizat role — only carries cnas-user.
        var user = await harness.SeedUserAsync(localLogin: "bob", password: "Aa1!aaaa",
            roles: ["cnas-user"]);

        var input = new LocalLoginInputDto("bob", "Aa1!aaaa");
        var result = await harness.Service.LoginAsync(input, null, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);

        await harness.Audit.Received().RecordAsync(
            "USER.LOGIN.WRONG_ROLE",
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
    public async Task LoginAsync_FiveConsecutiveFailures_LocksAccount()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(localLogin: "alice", password: "Aa1!aaaa",
            roles: ["utilizator-autorizat"]);

        // Five consecutive bad-password attempts trip the auto-lock threshold.
        for (var i = 0; i < 5; i++)
        {
            var bad = await harness.Service.LoginAsync(
                new LocalLoginInputDto("alice", "WrongPass99!"), null, null);
            bad.IsFailure.Should().BeTrue();
        }

        // The state-machine service was invoked with LockForFailedLoginsAsync.
        await harness.StateService.Received().LockForFailedLoginsAsync(
            user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_HappyPath_ReturnsTokenEnvelope_AndAudits_AndIncrementsMetric()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(localLogin: "alice", password: "Aa1!aaaa",
            roles: ["utilizator-autorizat"]);

        var input = new LocalLoginInputDto("alice", "Aa1!aaaa");
        var result = await harness.Service.LoginAsync(input, "192.168.1.1", "Mozilla/5.0");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.AccessToken.Should().Be(Harness.StubAccessToken);
        result.Value.RefreshToken.Should().Be(Harness.StubRefreshToken);
        result.Value.UserSqid.Should().Be($"SQID-{user.Id}");
        result.Value.DisplayName.Should().Be(user.DisplayName);
        result.Value.EffectiveRoles.Should().Contain("utilizator-autorizat");

        // Success audit + session registration both ran.
        await harness.Audit.Received().RecordAsync(
            "USER.LOGIN.SUCCESS",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await harness.SessionEnforcer.Received().RegisterNewSessionAsync(
            user.Id, Arg.Any<string>(), "192.168.1.1", "Mozilla/5.0", Arg.Any<CancellationToken>());

        // LastLoginUtc stamped using the injected clock — never DateTime.UtcNow.
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.Id == user.Id);
        reloaded.LastLoginUtc.Should().Be(ClockNow);

        // Failure counter reset on success.
        harness.FailureTracker.GetFailureCount(user.Id).Should().Be(0);
    }

    [Fact]
    public async Task LoginAsync_AccountLocked_ReturnsLoginInvalid_AndAudits()
    {
        var harness = Harness.Create();
        var user = await harness.SeedUserAsync(localLogin: "alice", password: "Aa1!aaaa",
            roles: ["utilizator-autorizat"], state: UserAccountState.Locked);

        var input = new LocalLoginInputDto("alice", "Aa1!aaaa");
        var result = await harness.Service.LoginAsync(input, null, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.LoginInvalid);

        await harness.Audit.Received().RecordAsync(
            "USER.LOGIN.ACCOUNT_NOT_ACTIVE",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserProfile),
            user.Id,
            Arg.Is<string>(s => s.Contains("Locked", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-locallogin-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Real in-memory failure tracker driven by the same stub clock as the service.
    /// Allows assertions on the counter without mocking — the tracker is intended
    /// to be a simple value-object under R0051 anyway.
    /// </summary>
    private sealed class TestFailureTracker(ICnasTimeProvider clock) : IFailedLoginAttemptTracker
    {
        private readonly InMemoryFailedLoginAttemptTracker _inner = new(clock);
        public int RecordFailure(long userId) => _inner.RecordFailure(userId);
        public int GetFailureCount(long userId) => _inner.GetFailureCount(userId);
        public void Reset(long userId) => _inner.Reset(userId);
    }

    private sealed class Harness
    {
        public const string StubAccessToken = "stub.jwt.access";
        public const string StubRefreshToken = "stub-opaque-refresh";

        public required CnasDbContext Db { get; init; }
        public required LocalLoginService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISessionLimitEnforcer SessionEnforcer { get; init; }
        public required IUserAccountStateService StateService { get; init; }
        public required IFailedLoginAttemptTracker FailureTracker { get; init; }
        public required Argon2idPasswordHasher Hasher { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);

            // Real Argon2id hasher — the tests want to exercise the actual hash /
            // verify round-trip so a wrong-password assertion is meaningful.
            var hasher = new Argon2idPasswordHasher();

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var jwtIssuer = Substitute.For<IJwtTokenIssuer>();
            jwtIssuer.IssueAccessToken(
                    Arg.Any<long>(),
                    Arg.Any<IReadOnlyCollection<string>>(),
                    Arg.Any<IReadOnlyCollection<string>>())
                .Returns((StubAccessToken, ClockNow.AddMinutes(15)));

            var refreshSvc = Substitute.For<IRefreshTokenService>();
            refreshSvc.IssueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Result<RefreshTokenIssueResult>.Success(
                    new RefreshTokenIssueResult(StubRefreshToken, Guid.NewGuid(),
                        ClockNow.AddDays(30), UserId: 7L)));

            var sessionEnforcer = Substitute.For<ISessionLimitEnforcer>();
            sessionEnforcer.RegisterNewSessionAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var stateSvc = Substitute.For<IUserAccountStateService>();
            stateSvc.LockForFailedLoginsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var groupResolver = Substitute.For<Cnas.Ps.Application.Identity.IUserGroupRoleResolver>();
            groupResolver.ResolveEffectiveRolesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<UserGroupEffectiveRolesDto>.Success(
                    new UserGroupEffectiveRolesDto("SQID-X",
                        Array.Empty<UserGroupEffectiveRoleDto>()))));

            var failureTracker = new TestFailureTracker(clock);

            var service = new LocalLoginService(
                db, clock, hasher, jwtIssuer, refreshSvc, sessionEnforcer,
                stateSvc, groupResolver, audit, failureTracker, sqids,
                NullLogger<LocalLoginService>.Instance);

            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                SessionEnforcer = sessionEnforcer,
                StateService = stateSvc,
                FailureTracker = failureTracker,
                Hasher = hasher,
            };
        }

        public async Task<UserProfile> SeedUserAsync(
            string localLogin,
            string password,
            IList<string> roles,
            UserAccountState state = UserAccountState.Active)
        {
            var entity = new UserProfile
            {
                DisplayName = "Alice Test",
                Email = "alice@test.md",
                LocalLogin = localLogin,
                LocalPasswordHash = Hasher.Hash(password),
                Roles = roles.ToList(),
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
