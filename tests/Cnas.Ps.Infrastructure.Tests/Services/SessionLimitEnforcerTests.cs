using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R2264 / SEC 017 — tests for <see cref="SessionLimitEnforcer"/>. Verifies the
/// FIFO eviction policy fires only when the per-user ceiling would be exceeded,
/// writes the Critical audit row, and stamps the canonical termination reason on
/// the evicted row.
/// </summary>
public sealed class SessionLimitEnforcerTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RegisterNewSessionAsync_FirstSession_InsertsRowAndDoesNotEvict()
    {
        var h = Harness.Create(maxConcurrent: 3);

        var outcome = await h.Service.RegisterNewSessionAsync(
            userId: 100L, sessionId: "jti-1", ipAddress: "127.0.0.1", userAgent: "agent");

        outcome.IsSuccess.Should().BeTrue();
        var rows = await h.Db.UserSessions.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].SessionId.Should().Be("jti-1");
        rows[0].IsTerminated.Should().BeFalse();
        await h.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task RegisterNewSessionAsync_WhenLimitExceeded_EvictsOldestAndAuditsCritical()
    {
        var h = Harness.Create(maxConcurrent: 3);
        // Pre-seed 3 existing sessions for the user, oldest first.
        for (var i = 0; i < 3; i++)
        {
            h.Db.UserSessions.Add(new UserSession
            {
                UserUserId = 100L,
                SessionId = $"old-{i}",
                LastActivityUtc = ClockNow.AddMinutes(-30 + i),
                CreatedAtUtc = ClockNow.AddMinutes(-30 + i),
                IsActive = true,
            });
        }
        await h.Db.SaveChangesAsync();

        var outcome = await h.Service.RegisterNewSessionAsync(
            userId: 100L, sessionId: "jti-new", ipAddress: null, userAgent: null);

        outcome.IsSuccess.Should().BeTrue();
        var evicted = await h.Db.UserSessions.SingleAsync(s => s.SessionId == "old-0");
        evicted.IsTerminated.Should().BeTrue();
        evicted.TerminationReason.Should().Be(SessionLimitEnforcer.EvictionReason);
        evicted.TerminatedAtUtc.Should().Be(ClockNow);
        await h.Audit.Received(1).RecordAsync(
            "USER.SESSION.TERMINATED_BY_LIMIT",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(UserSession),
            evicted.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterNewSessionAsync_AtLimit_DoesNotEvict()
    {
        // 2 existing rows + 1 new row = 3 (equals the ceiling). No eviction.
        var h = Harness.Create(maxConcurrent: 3);
        for (var i = 0; i < 2; i++)
        {
            h.Db.UserSessions.Add(new UserSession
            {
                UserUserId = 100L,
                SessionId = $"old-{i}",
                LastActivityUtc = ClockNow.AddMinutes(-30 + i),
                CreatedAtUtc = ClockNow.AddMinutes(-30 + i),
                IsActive = true,
            });
        }
        await h.Db.SaveChangesAsync();

        var outcome = await h.Service.RegisterNewSessionAsync(100L, "jti-new", null, null);

        outcome.IsSuccess.Should().BeTrue();
        var rows = await h.Db.UserSessions.Where(s => !s.IsTerminated).ToListAsync();
        rows.Should().HaveCount(3);
        await h.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-sess-limit-{Guid.NewGuid():N}")
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
        public required SessionLimitEnforcer Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create(int maxConcurrent)
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("CALLER-SQID");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var opts = Options.Create(new SessionLimitOptions { MaxConcurrentSessions = maxConcurrent });
            var svc = new SessionLimitEnforcer(db, clock, caller, audit, opts);
            return new Harness { Db = db, Service = svc, Audit = audit };
        }
    }
}
