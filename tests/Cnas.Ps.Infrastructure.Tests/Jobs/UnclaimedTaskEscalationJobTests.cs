using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Tests for <see cref="UnclaimedTaskEscalationJob"/> — the hourly sweep that finds
/// <see cref="WorkflowTask"/> rows parked in a group inbox (
/// <c>Status == Pending &amp;&amp; AssignedUserId == null</c>) past the configured
/// <see cref="UnclaimedTaskEscalationOptions.TimeoutWindow"/> and emits an audit +
/// counter signal (R0202 / CF 20.05).
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job emits on the static meter
/// (<c>cnas.workflow.task.escalated</c>) so cross-test parallelism must be suppressed
/// to keep increment-count assertions stable.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class UnclaimedTaskEscalationJobTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_NoTasksAtAll_DoesNothing()
    {
        var harness = await Harness.CreateAsync();

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_TaskClaimed_NotEscalated()
    {
        var harness = await Harness.CreateAsync();
        // Task is claimed (AssignedUserId set) — even if UnclaimedSinceUtc were set, the
        // job's AssignedUserId == null predicate guards against escalation.
        await harness.SeedTaskAsync(
            assignedUserId: 42L,
            unclaimedSinceUtc: ClockNow.AddHours(-24));

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_TaskUnclaimedRecent_NotEscalated()
    {
        var harness = await Harness.CreateAsync();
        // Inside the 4-hour window — must not escalate.
        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-1));

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_TaskUnclaimedPastWindow_Escalated()
    {
        using var capture = new MetricCapture("cnas.workflow.task.escalated");
        var harness = await Harness.CreateAsync();
        var taskId = await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-5));

        await harness.Job.Execute(FakeContext());

        // Audit was emitted with the stable event code + entity reference.
        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOW_TASK.ESCALATED",
            AuditSeverity.Notice,
            "system:r0202-escalation",
            nameof(WorkflowTask),
            taskId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Counter incremented exactly once.
        capture.TotalIncrement.Should().Be(1);

        // Idempotency anchor: the stamp is cleared so the row no longer matches the
        // predicate on a follow-up fire.
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == taskId);
        reloaded.UnclaimedSinceUtc.Should().BeNull();
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
        // The task itself is NOT mutated to a different status — escalation is a signal.
        reloaded.Status.Should().Be(WorkflowTaskStatus.Pending);
        reloaded.AssignedUserId.Should().BeNull();
    }

    [Fact]
    public async Task Execute_TaskWithoutUnclaimedStamp_Skipped()
    {
        // Tasks created without GroupCode (direct-assignment workflow) never have
        // UnclaimedSinceUtc populated. The job's predicate excludes rows where the stamp
        // is null, so a task without it is invisible to the escalation sweep.
        var harness = await Harness.CreateAsync();
        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: null,
            groupCode: null);

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_AlreadyEscalated_NotEscalatedAgain()
    {
        // After the first fire flips UnclaimedSinceUtc to null, a second fire on the same
        // data must be a no-op. This is the canonical idempotency invariant.
        var harness = await Harness.CreateAsync();
        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-10));

        await harness.Job.Execute(FakeContext());
        harness.Audit.ClearReceivedCalls();

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_TaskInactive_NotEscalated()
    {
        var harness = await Harness.CreateAsync();
        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-10),
            isActive: false);

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_TaskCompleted_NotEscalated()
    {
        var harness = await Harness.CreateAsync();
        // A row whose Status is Completed must never be escalated, even if its stamp
        // somehow lingered (it shouldn't — writer invariant clears it on claim).
        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-10),
            status: WorkflowTaskStatus.Completed);

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_MaxBatchSize_LimitsBatchSize()
    {
        using var capture = new MetricCapture("cnas.workflow.task.escalated");
        // Configure a small cap so the test stays fast.
        var harness = await Harness.CreateAsync(
            options: new UnclaimedTaskEscalationOptions { MaxBatchSize = 3 });
        for (var i = 0; i < 7; i++)
        {
            await harness.SeedTaskAsync(
                assignedUserId: null,
                unclaimedSinceUtc: ClockNow.AddHours(-(5 + i)));
        }

        await harness.Job.Execute(FakeContext());

        // Only 3 rows are escalated on this fire — the cap holds.
        capture.TotalIncrement.Should().Be(3);
        await harness.Audit.Received(3).RecordAsync(
            "WORKFLOW_TASK.ESCALATED", Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_AuditDetails_HasNoPii()
    {
        var harness = await Harness.CreateAsync();
        var captured = new List<string>();
        harness.Audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(),
                Arg.Do<string>(d => captured.Add(d)),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        await harness.SeedTaskAsync(
            assignedUserId: null,
            unclaimedSinceUtc: ClockNow.AddHours(-10),
            groupCode: "cnas-examiner");

        await harness.Job.Execute(FakeContext());

        captured.Should().HaveCount(1);
        var details = captured[0];
        // The group code (team identifier — not PII) is present.
        details.Should().Contain("cnas-examiner");
        details.Should().Contain("unclaimed_timeout");
        // Must be valid JSON.
        using var doc = JsonDocument.Parse(details);
        // No 13-digit IDNP/IDNO numeric runs in the payload.
        System.Text.RegularExpressions.Regex.IsMatch(details, @"\d{13}").Should().BeFalse();
        // No email patterns.
        System.Text.RegularExpressions.Regex.IsMatch(details, @"[\w\.-]+@[\w\.-]+").Should().BeFalse();
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Returns a no-op <see cref="IJobExecutionContext"/> with a cancellation token.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Disposes the listener at the end of the test
    /// so the next test starts from a clean slate.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(); }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate) { _measurements.Add(value); }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UnclaimedTaskEscalationJob Job { get; init; }
        public required IAuditService Audit { get; init; }

        private long _nextTaskKey = 1;

        public static async Task<Harness> CreateAsync(UnclaimedTaskEscalationOptions? options = null)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-unclaimed-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var clock = new StubClock(ClockNow);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            var scope = Substitute.For<IServiceScope>();
            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(ICnasDbContext)).Returns(db);
            sp.GetService(typeof(IAuditService)).Returns(audit);
            scope.ServiceProvider.Returns(sp);
            scopeFactory.CreateScope().Returns(scope);

            var resolvedOptions = options ?? new UnclaimedTaskEscalationOptions();
            var optionsWrap = Options.Create(resolvedOptions);

            var job = new UnclaimedTaskEscalationJob(
                scopeFactory,
                clock,
                optionsWrap,
                NullLogger<UnclaimedTaskEscalationJob>.Instance);

            // Seed a minimal solicitant/passport/app/dossier graph once so per-test
            // SeedTaskAsync can attach to a real DossierId.
            var harness = new Harness { Db = db, Job = job, Audit = audit };
            await harness.SeedDossierAsync().ConfigureAwait(false);
            return harness;
        }

        public long DossierId { get; private set; }

        private async Task SeedDossierAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test User",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-TEST",
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
                DossierNumber = $"D-2026-{Guid.NewGuid():N}"[..14],
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            DossierId = dossier.Id;
        }

        public async Task<long> SeedTaskAsync(
            long? assignedUserId,
            DateTime? unclaimedSinceUtc,
            string? groupCode = "cnas-examiner",
            WorkflowTaskStatus status = WorkflowTaskStatus.Pending,
            bool isActive = true)
        {
            var key = _nextTaskKey++;
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow.AddDays(-1),
                DossierId = DossierId,
                Title = $"Test task #{key}",
                Status = status,
                AssignedUserId = assignedUserId,
                GroupCode = groupCode,
                UnclaimedSinceUtc = unclaimedSinceUtc,
                IsActive = isActive,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync();
            return task.Id;
        }
    }
}
