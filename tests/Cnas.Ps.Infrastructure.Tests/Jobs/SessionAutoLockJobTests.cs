using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R2267 / SEC 020 — tests for <see cref="SessionAutoLockJob"/>. Verifies the
/// idle predicate, idempotency on already-locked rows, and the counter increment.
/// </summary>
public sealed class SessionAutoLockJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_IdleSession_FlipsToLockedAndAudits()
    {
        var h = await Harness.CreateAsync(idleMinutes: 15);
        var row = new UserSession
        {
            UserUserId = 100L,
            SessionId = "jti-idle",
            LastActivityUtc = ClockNow.AddMinutes(-20), // past the 15-minute threshold
            CreatedAtUtc = ClockNow.AddMinutes(-60),
            IsActive = true,
            IsLocked = false,
            IsTerminated = false,
        };
        h.Db.UserSessions.Add(row);
        await h.Db.SaveChangesAsync();

        await h.Job.Execute(FakeContext());

        var reloaded = await h.Db.UserSessions.SingleAsync(s => s.Id == row.Id);
        reloaded.IsLocked.Should().BeTrue();
        reloaded.LockedAtUtc.Should().Be(ClockNow);
        await h.Audit.Received(1).RecordAsync(
            "USER.SESSION.LOCKED_AUTO",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(UserSession),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_AlreadyLockedSession_Skipped()
    {
        var h = await Harness.CreateAsync(idleMinutes: 15);
        h.Db.UserSessions.Add(new UserSession
        {
            UserUserId = 100L,
            SessionId = "jti-locked",
            LastActivityUtc = ClockNow.AddMinutes(-30),
            CreatedAtUtc = ClockNow.AddMinutes(-60),
            IsActive = true,
            IsLocked = true,
            LockedAtUtc = ClockNow.AddMinutes(-25),
            IsTerminated = false,
        });
        await h.Db.SaveChangesAsync();

        await h.Job.Execute(FakeContext());

        await h.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_IdleSession_IncrementsCounter()
    {
        // Snapshot the counter via a MeterListener — process-static state, see CnasMeter remarks.
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == "cnas.session.auto_locked")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        var h = await Harness.CreateAsync(idleMinutes: 15);
        h.Db.UserSessions.Add(new UserSession
        {
            UserUserId = 100L,
            SessionId = "jti-idle-cnt",
            LastActivityUtc = ClockNow.AddMinutes(-20),
            CreatedAtUtc = ClockNow.AddMinutes(-60),
            IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        await h.Job.Execute(FakeContext());

        listener.RecordObservableInstruments();
        observed.Should().BeGreaterThan(0);
    }

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required SessionAutoLockJob Job { get; init; }
        public required IAuditService Audit { get; init; }

        public static async Task<Harness> CreateAsync(int idleMinutes)
        {
            await Task.Yield();
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-autolock-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(ICnasDbContext)).Returns(db);
            sp.GetService(typeof(IAuditService)).Returns(audit);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var clock = new StubClock(ClockNow);
            var optsValue = Options.Create(new SessionLimitOptions { IdleLockMinutes = idleMinutes });
            var job = new SessionAutoLockJob(
                scopeFactory, clock,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                optsValue, NullLogger<SessionAutoLockJob>.Instance);

            return new Harness { Db = db, Job = job, Audit = audit };
        }
    }
}
