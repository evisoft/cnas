using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0128 / R0173 — behaviour tests for
/// <see cref="WorkflowNotificationOrchestrator"/>. Each test exercises a single contract
/// — legacy default fallback, explicit suppression, multi-recipient fan-out, quiet-hours
/// scheduling, template-override propagation, and the wrap-window quiet-hours math.
/// </summary>
/// <remarks>
/// The orchestrator emits on the process-static <see cref="CnasMeter"/> so the class is
/// a member of <see cref="CnasMeterCollection"/> to suppress cross-file parallelism.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class WorkflowNotificationOrchestratorTests
{
    private const long WorkflowId = 1001;
    private const long TaskId = 5050;
    private const long AssigneeId = 2002;
    private const long ApplicantUserId = 3003;

    [Fact]
    public async Task NoStrategy_FallsBackToLegacyDefault_NotifiesAssignee()
    {
        // Arrange — no strategy configured for (workflow, event).
        var harness = await Harness.CreateAsync();

        // Act
        var result = await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert — exactly one EnqueueAsync to the assignee.
        result.IsSuccess.Should().BeTrue();
        await harness.Notify.Received(1).EnqueueAsync(
            AssigneeId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrategyDisabled_SuppressesDispatch_AndIncrementsCounter()
    {
        // Arrange — strategy exists with IsEnabled=false.
        var harness = await Harness.CreateAsync();
        await harness.SeedStrategyAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = WorkflowId,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = false,
            Channels = new List<NotificationChannel>(),
            RecipientRoles = new List<string>(),
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await harness.Resolver.InvalidateAsync();

        using var capture = new MetricCapture("cnas.workflow.notify.suppressed");

        // Act
        var result = await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert — no dispatch, counter +1.
        result.IsSuccess.Should().BeTrue();
        await harness.Notify.DidNotReceive().EnqueueAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task Strategy_WithAssigneeAndApplicant_FansOutToBothRecipients()
    {
        // Arrange — strategy targets both assignee and applicant.
        var harness = await Harness.CreateAsync();
        await harness.SeedStrategyAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = WorkflowId,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = true,
            Channels = new List<NotificationChannel> { NotificationChannel.InApp },
            RecipientRoles = new List<string> { "Assignee", "Applicant" },
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await harness.Resolver.InvalidateAsync();

        // Act
        var result = await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert — both recipients invoked exactly once.
        result.IsSuccess.Should().BeTrue();
        await harness.Notify.Received(1).EnqueueAsync(
            AssigneeId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.Notify.Received(1).EnqueueAsync(
            ApplicantUserId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuietHoursInside_TagsSubjectWithDeferredHint()
    {
        // Arrange — Chisinau local time at the harness clock instant falls INSIDE the
        // configured quiet window. Clock = 2026-05-21 23:30 UTC → 02:30 local
        // (Europe/Chisinau is UTC+3 in May). Window 22:00..06:00 includes 02:30.
        var harness = await Harness.CreateAsync(clockUtc: new DateTime(2026, 5, 21, 23, 30, 0, DateTimeKind.Utc));
        await harness.SeedStrategyAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = WorkflowId,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = true,
            Channels = new List<NotificationChannel> { NotificationChannel.Email },
            RecipientRoles = new List<string> { "Assignee" },
            QuietHoursStartLocalMinute = 22 * 60,
            QuietHoursEndLocalMinute = 6 * 60,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await harness.Resolver.InvalidateAsync();

        string? capturedSubject = null;
        harness.Notify
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedSubject = call.ArgAt<string>(1); // positional arg 1 = subject
                return Task.FromResult(Result.Success());
            });

        // Act
        var result = await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        capturedSubject.Should().NotBeNull();
        capturedSubject!.Should().Contain("deferred-until=");
    }

    [Fact]
    public async Task QuietHoursOutside_DispatchesImmediately_NoDeferralTag()
    {
        // Arrange — Clock = 2026-05-21 10:00 UTC → 13:00 local (May, UTC+3). Outside
        // the 22:00..06:00 quiet window — dispatch should be immediate.
        var harness = await Harness.CreateAsync(clockUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc));
        await harness.SeedStrategyAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = WorkflowId,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = true,
            Channels = new List<NotificationChannel> { NotificationChannel.Email },
            RecipientRoles = new List<string> { "Assignee" },
            QuietHoursStartLocalMinute = 22 * 60,
            QuietHoursEndLocalMinute = 6 * 60,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await harness.Resolver.InvalidateAsync();

        string? capturedSubject = null;
        harness.Notify
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedSubject = call.ArgAt<string>(1);
                return Task.FromResult(Result.Success());
            });

        // Act
        await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert
        capturedSubject.Should().NotBeNull();
        capturedSubject!.Should().NotContain("deferred-until=");
    }

    [Fact]
    public async Task TemplateCodeOverride_AppearsInSubject()
    {
        // Arrange
        var harness = await Harness.CreateAsync();
        await harness.SeedStrategyAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = WorkflowId,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = true,
            Channels = new List<NotificationChannel> { NotificationChannel.InApp },
            RecipientRoles = new List<string> { "Assignee" },
            TemplateCodeOverride = "PENSION_ASSIGN_V2",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await harness.Resolver.InvalidateAsync();

        string? capturedSubject = null;
        harness.Notify
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedSubject = call.ArgAt<string>(1);
                return Task.FromResult(Result.Success());
            });

        // Act
        await harness.Orchestrator.DispatchAsync(
            WorkflowId, TaskId, WorkflowNotificationEvents.TaskAssigned, templateContext: null);

        // Assert
        capturedSubject.Should().NotBeNull();
        capturedSubject!.Should().Contain("PENSION_ASSIGN_V2");
    }

    [Fact]
    public void IsInsideWindow_WrappingWindow_2330_IsInside_22To06()
    {
        // 23:30 = 23*60 + 30 = 1410. Window 22:00..06:00 = 1320..360 (wraps).
        WorkflowNotificationOrchestrator.IsInsideWindow(minute: 1410, start: 22 * 60, end: 6 * 60)
            .Should().BeTrue();
        // 13:00 = 780 — outside the wrap window.
        WorkflowNotificationOrchestrator.IsInsideWindow(minute: 780, start: 22 * 60, end: 6 * 60)
            .Should().BeFalse();
        // 03:00 = 180 — inside the wrap window (after-midnight portion).
        WorkflowNotificationOrchestrator.IsInsideWindow(minute: 180, start: 22 * 60, end: 6 * 60)
            .Should().BeTrue();
    }

    /// <summary>Deterministic clock supplied to the orchestrator.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Captures the increments of a single CnasMeter counter via MeterListener.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) { return _measurements.Sum(); } }
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
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_gate) { _measurements.Add(value); }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Harness wiring an in-memory DB, the resolver, the orchestrator, and a mock dispatcher.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required WorkflowNotificationStrategyResolver Resolver { get; init; }
        public required WorkflowNotificationOrchestrator Orchestrator { get; init; }
        public required INotificationService Notify { get; init; }

        public static async Task<Harness> CreateAsync(DateTime? clockUtc = null)
        {
            var dbName = $"cnas-wf-notify-orch-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            // Standalone Db used by the harness for seeding — shares the named in-memory store.
            var standaloneOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(standaloneOpts);

            // Seed: workflow task with an assigned user + an applicant chain (Dossier →
            // Application → Solicitant → UserProfile).
            var task = new WorkflowTask
            {
                Id = TaskId,
                DossierId = 8888,
                Title = "Examinare",
                Status = WorkflowTaskStatus.Pending,
                AssignedUserId = AssigneeId,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            };
            db.WorkflowTasks.Add(task);

            // Applicant chain: Dossier → Application → Solicitant. The orchestrator maps
            // the Solicitant.NationalIdHash to a UserProfile to resolve the citizen user.
            const string applicantNationalIdHash = "TEST_HASH_APPLICANT";
            db.Solicitants.Add(new Solicitant
            {
                Id = 7777,
                NationalId = "ENC-IDNP",
                NationalIdHash = applicantNationalIdHash,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Applicant",
                PreferredLanguage = "ro",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            db.Applications.Add(new ServiceApplication
            {
                Id = 6666,
                SolicitantId = 7777,
                ServicePassportId = 1,
                Status = ApplicationStatus.UnderExamination,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            db.Dossiers.Add(new Dossier
            {
                Id = 8888,
                ApplicationId = 6666,
                DossierNumber = "D-0001",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            db.UserProfiles.Add(new UserProfile
            {
                Id = ApplicantUserId,
                MPassSubject = "applicant-sub",
                DisplayName = "Applicant User",
                NationalIdHash = applicantNationalIdHash,
                PreferredLanguage = "ro",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var resolver = new WorkflowNotificationStrategyResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowNotificationStrategyResolver>.Instance);

            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(99L);
            caller.UserSqid.Returns("SQID-CALLER");
            caller.CorrelationId.Returns("corr-1");

            // Resolve the read-only DbContext via the provider (per-request scope) — but the
            // orchestrator-side read flow must match the standalone Db; we use the provider's
            // scoped context so EF Core sees the seeded rows through the shared in-memory store.
            var scope = provider.CreateScope();
            var readDb = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

            var orchestrator = new WorkflowNotificationOrchestrator(
                resolver,
                notify,
                readDb,
                new StubClock(clockUtc ?? DateTime.UtcNow),
                caller,
                NullLogger<WorkflowNotificationOrchestrator>.Instance);

            return new Harness
            {
                Db = db,
                Resolver = resolver,
                Orchestrator = orchestrator,
                Notify = notify,
            };
        }

        public async Task SeedStrategyAsync(WorkflowNotificationStrategy row)
        {
            Db.WorkflowNotificationStrategies.Add(row);
            await Db.SaveChangesAsync();
        }
    }
}
