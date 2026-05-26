using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0127 / CF 16.11 — tests for <see cref="UserAbsenceLifecycleJob"/>. Verifies both
/// lifecycle transitions: <c>Planned → Active</c> when the start date is reached and
/// <c>Active → Completed</c> when the end date has elapsed.
/// </summary>
public sealed class UserAbsenceLifecycleJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_PlannedAbsenceWhoseStartDateReached_Activates()
    {
        // Arrange
        var h = await Harness.CreateAsync();
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow.AddDays(-10),
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(-1), // past
            EndDateUtc = ClockNow.AddDays(5),
            Reason = "Concediu",
            Status = UserAbsenceStatus.Planned,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        h.Service.ActivateAsync(absence.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        await h.Job.Execute(FakeContext());

        // Assert
        await h.Service.Received(1).ActivateAsync(absence.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ActiveAbsencePastEndDate_Completes()
    {
        var h = await Harness.CreateAsync();
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow.AddDays(-30),
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(-20),
            EndDateUtc = ClockNow.AddDays(-1), // ended yesterday
            Reason = "Concediu",
            Status = UserAbsenceStatus.Active,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        h.Service.CompleteAsync(absence.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        await h.Job.Execute(FakeContext());

        await h.Service.Received(1).CompleteAsync(absence.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_PlannedAbsenceNotYetStarted_DoesNothing()
    {
        var h = await Harness.CreateAsync();
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(5), // future
            EndDateUtc = ClockNow.AddDays(10),
            Reason = "Concediu",
            Status = UserAbsenceStatus.Planned,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        await h.Job.Execute(FakeContext());

        await h.Service.DidNotReceive().ActivateAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await h.Service.DidNotReceive().CompleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ────────────────────────── helpers ──────────────────────────

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UserAbsenceLifecycleJob Job { get; init; }
        public required IUserAbsenceService Service { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            await Task.Yield();
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-absence-job-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var service = Substitute.For<IUserAbsenceService>();

            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(ICnasDbContext)).Returns(db);
            sp.GetService(typeof(IUserAbsenceService)).Returns(service);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var clock = new StubClock(ClockNow);
            var job = new UserAbsenceLifecycleJob(
                scopeFactory, clock,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                NullLogger<UserAbsenceLifecycleJob>.Instance);

            return new Harness { Db = db, Job = job, Service = service };
        }
    }
}
