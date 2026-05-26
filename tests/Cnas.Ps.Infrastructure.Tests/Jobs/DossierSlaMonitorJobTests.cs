using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="DossierSlaMonitorJob"/>. The job picks up Pending/InProgress
/// workflow tasks whose <c>DueAtUtc</c> is in the past, flips them to
/// <see cref="WorkflowTaskStatus.Overdue"/>, and notifies the assignee.
/// Idempotency: a task already in <see cref="WorkflowTaskStatus.Overdue"/> is left alone.
/// </summary>
public class DossierSlaMonitorJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_NoTasks_DoesNothing()
    {
        var harness = Harness.Create();
        var ctx = FakeContext();

        await harness.Job.Execute(ctx);

        await harness.Notify.DidNotReceive().EnqueueAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_OverdueTask_TransitionsToOverdueAndNotifies()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync("D-2026-AAAAA001");
        var task = await harness.SeedTaskAsync(
            dossierId,
            status: WorkflowTaskStatus.Pending,
            dueAtUtc: ClockNow.AddHours(-1),
            assignedUserId: 42L,
            title: "Examinare cerere");

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(WorkflowTaskStatus.Overdue);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Notify.Received(1).EnqueueAsync(
            42L,
            "Sarcină depășită",
            Arg.Is<string>(b => b.Contains("Examinare cerere") && b.Contains("D-2026-AAAAA001")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_AlreadyOverdueTask_DoesNotDoubleNotify()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync("D-2026-AAAAA002");
        await harness.SeedTaskAsync(
            dossierId,
            status: WorkflowTaskStatus.Overdue,
            dueAtUtc: ClockNow.AddDays(-3),
            assignedUserId: 42L);

        await harness.Job.Execute(FakeContext());

        // Idempotency: re-running the monitor must not re-notify already-overdue rows.
        await harness.Notify.DidNotReceive().EnqueueAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_FutureTask_LeavesAlone()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync("D-2026-AAAAA003");
        var task = await harness.SeedTaskAsync(
            dossierId,
            status: WorkflowTaskStatus.Pending,
            dueAtUtc: ClockNow.AddDays(2),
            assignedUserId: 42L);

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(WorkflowTaskStatus.Pending);
        reloaded.UpdatedAtUtc.Should().BeNull();
        await harness.Notify.DidNotReceive().EnqueueAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test plumbing ───────────────────────

    /// <summary>Returns a no-op <see cref="IJobExecutionContext"/> with a cancellation token.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-{Guid.NewGuid():N}")
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
        public required DossierSlaMonitorJob Job { get; init; }
        public required INotificationService Notify { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var job = new DossierSlaMonitorJob(db, clock, notify, NullLogger<DossierSlaMonitorJob>.Instance);
            return new Harness { Db = db, Job = job, Notify = notify };
        }

        public async Task<long> SeedDossierAsync(string dossierNumber)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = $"2{Random.Shared.NextInt64(1_000_000_000_000L, 9_999_999_999_999L)}"[..13],
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = $"SP-{Guid.NewGuid():N}"[..16],
                NameRo = "Test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = dossierNumber,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        public async Task<WorkflowTask> SeedTaskAsync(
            long dossierId,
            WorkflowTaskStatus status,
            DateTime? dueAtUtc,
            long? assignedUserId,
            string title = "Examinare cerere")
        {
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossierId,
                Title = title,
                Status = status,
                AssignedUserId = assignedUserId,
                GroupCode = "cnas-examiner",
                DueAtUtc = dueAtUtc,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync();
            return task;
        }
    }
}
