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
/// R2267 / SEC 020 — tests for <see cref="SessionLockService"/>. Verifies the
/// lock / unlock / probe / admin-terminate primitives drive the correct row
/// mutations and emit the canonical audit codes.
/// </summary>
public sealed class SessionLockServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LockCurrentSessionAsync_FlipsIsLockedAndAuditsNotice()
    {
        var h = Harness.Create(sessionId: "JTI-1");
        var row = await h.SeedSessionAsync(userId: 100L, sessionId: "JTI-1");

        var outcome = await h.Service.LockCurrentSessionAsync();

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.IsLocked.Should().BeTrue();
        var reloaded = await h.Db.UserSessions.SingleAsync(s => s.Id == row.Id);
        reloaded.IsLocked.Should().BeTrue();
        reloaded.LockedAtUtc.Should().Be(ClockNow);
        await h.Audit.Received(1).RecordAsync(
            "USER.SESSION.LOCKED_MANUAL",
            AuditSeverity.Notice,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockCurrentSessionAsync_FlipsIsLockedBackToFalse()
    {
        var h = Harness.Create(sessionId: "JTI-1");
        var row = await h.SeedSessionAsync(100L, "JTI-1", isLocked: true);

        var outcome = await h.Service.UnlockCurrentSessionAsync();

        outcome.IsSuccess.Should().BeTrue();
        var reloaded = await h.Db.UserSessions.SingleAsync(s => s.Id == row.Id);
        reloaded.IsLocked.Should().BeFalse();
        reloaded.LockedAtUtc.Should().BeNull();
        await h.Audit.Received(1).RecordAsync(
            "USER.SESSION.UNLOCKED_MANUAL",
            AuditSeverity.Notice,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsLockedAsync_ReturnsTrueAfterLock()
    {
        var h = Harness.Create(sessionId: "JTI-1");
        await h.SeedSessionAsync(100L, "JTI-1", isLocked: true);

        var locked = await h.Service.IsLockedAsync("JTI-1");

        locked.Should().BeTrue();
    }

    [Fact]
    public async Task IsLockedAsync_ReturnsFalseForLiveSession()
    {
        var h = Harness.Create(sessionId: "JTI-1");
        await h.SeedSessionAsync(100L, "JTI-1");

        var locked = await h.Service.IsLockedAsync("JTI-1");

        locked.Should().BeFalse();
    }

    [Fact]
    public async Task LockCurrentSessionAsync_NoSessionId_ReturnsUnauthorized()
    {
        var h = Harness.Create(sessionId: null);

        var outcome = await h.Service.LockCurrentSessionAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-sess-lock-{Guid.NewGuid():N}")
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
        public required SessionLockService Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create(string? sessionId)
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string>()).Returns(call =>
            {
                var s = call.Arg<string>();
                if (s.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(s[5..], out var id))
                {
                    return Result<long>.Success(id);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(100L);
            caller.UserSqid.Returns("SQID-100");
            caller.Roles.Returns(["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");
            caller.SessionId.Returns(sessionId);

            var svc = new SessionLockService(db, sqids, clock, caller, audit);
            return new Harness { Db = db, Service = svc, Audit = audit };
        }

        public async Task<UserSession> SeedSessionAsync(long userId, string sessionId, bool isLocked = false)
        {
            var row = new UserSession
            {
                UserUserId = userId,
                SessionId = sessionId,
                LastActivityUtc = ClockNow.AddMinutes(-1),
                CreatedAtUtc = ClockNow.AddMinutes(-5),
                IsActive = true,
                IsLocked = isLocked,
                LockedAtUtc = isLocked ? ClockNow.AddMinutes(-2) : null,
            };
            Db.UserSessions.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }
    }
}
