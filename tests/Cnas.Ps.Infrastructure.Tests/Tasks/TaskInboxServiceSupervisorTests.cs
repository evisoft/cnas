using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Tasks;

/// <summary>
/// R0381 / UC05 — integration tests for the supervisor surface on
/// <see cref="TaskInboxService"/>: <c>ListTeamQueueAsync</c> (peer-of-supervisor
/// scoping + optional assignee filter) and the Sqid-typed
/// <c>ReassignTaskAsync</c> facade. Each test exercises the SUT against an EF
/// Core InMemory backend, isolating row mutation / Result shape / observability.
/// </summary>
/// <remarks>
/// <para>
/// Written test-first per CLAUDE.md RULE 1 — the assertions FAIL until the
/// supervisor extensions land on <see cref="ITaskInboxService"/>.
/// </para>
/// <para>
/// The harness mirrors the conventions of <c>TaskInboxServiceReassignTests</c>:
/// deterministic clock, NSubstitute audit + orchestrator, EF InMemory provider,
/// and a fresh DB name per test so suite-wide parallelism is safe.
/// </para>
/// </remarks>
public sealed class TaskInboxServiceSupervisorTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Internal id of the supervisor under test.</summary>
    private const long SupervisorId = 1000L;

    [Fact]
    public async Task ListTeamQueueAsync_NoGroupMemberships_ReturnsEmptyPage()
    {
        // Arrange — the supervisor is not a member of any group, so the team queue
        //           must be empty (no peers to surface tasks for).
        var harness = await TestHarness.CreateAsync();
        // Seed an unrelated task that belongs to someone outside any of our groups —
        // it must NOT appear in the supervisor's team view.
        await harness.SeedUserAsync(2000L);
        var dossierId = await harness.SeedDossierAsync();
        await harness.SeedTaskAsync(dossierId, assignedUserId: 2000L);

        // Act
        var result = await harness.Service.ListTeamQueueAsync(
            assigneeFilterSqid: null, page: 1, pageSize: 20);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTeamQueueAsync_SingleTeamWithPeerTasks_ReturnsOnlyPeerTasks()
    {
        // Arrange — supervisor + one peer share group G1; tasks live on both.
        var harness = await TestHarness.CreateAsync();
        var peerId = 2001L;
        await harness.SeedUserAsync(peerId, displayName: "Peer Ana");
        var groupId = await harness.SeedGroupAsync("G_OFFICE");
        await harness.AddMembershipAsync(groupId, SupervisorId);
        await harness.AddMembershipAsync(groupId, peerId);

        var dossierId = await harness.SeedDossierAsync();
        var peerTask = await harness.SeedTaskAsync(dossierId, assignedUserId: peerId,
            title: "Peer task");
        // Supervisor's own task — must be excluded from the team view.
        var ownTask = await harness.SeedTaskAsync(dossierId, assignedUserId: SupervisorId,
            title: "Own task");

        // Act
        var result = await harness.Service.ListTeamQueueAsync(
            assigneeFilterSqid: null, page: 1, pageSize: 20);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Single();
        row.Title.Should().Be("Peer task");
        row.AssigneeDisplayName.Should().Be("Peer Ana");
        row.AssigneeSqid.Should().NotBeNullOrEmpty();
        // Supervisor's own task is filtered out.
        result.Value.Items.Should().NotContain(r => r.Title == "Own task");
        // Negative anti-coupling — own task id never appears in the page.
        result.Value.Items.Should().NotContain(r => r.Id == $"SQID-{ownTask.Id}");
    }

    [Fact]
    public async Task ListTeamQueueAsync_MultipleTeams_AggregatesAllPeers()
    {
        // Arrange — supervisor is in two groups; one peer in each.
        var harness = await TestHarness.CreateAsync();
        await harness.SeedUserAsync(3001L, displayName: "Peer Alpha");
        await harness.SeedUserAsync(3002L, displayName: "Peer Beta");

        var g1 = await harness.SeedGroupAsync("G_TEAM_A");
        var g2 = await harness.SeedGroupAsync("G_TEAM_B");
        await harness.AddMembershipAsync(g1, SupervisorId);
        await harness.AddMembershipAsync(g1, 3001L);
        await harness.AddMembershipAsync(g2, SupervisorId);
        await harness.AddMembershipAsync(g2, 3002L);

        var dossierId = await harness.SeedDossierAsync();
        await harness.SeedTaskAsync(dossierId, assignedUserId: 3001L, title: "A-task");
        await harness.SeedTaskAsync(dossierId, assignedUserId: 3002L, title: "B-task");

        // Act
        var result = await harness.Service.ListTeamQueueAsync(
            assigneeFilterSqid: null, page: 1, pageSize: 20);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(i => i.Title).Should()
            .BeEquivalentTo(["A-task", "B-task"]);
    }

    [Fact]
    public async Task ListTeamQueueAsync_AssigneeFilter_NarrowsToSinglePeer()
    {
        var harness = await TestHarness.CreateAsync();
        var peer1 = 4001L;
        var peer2 = 4002L;
        await harness.SeedUserAsync(peer1, displayName: "Filtered Peer");
        await harness.SeedUserAsync(peer2, displayName: "Other Peer");
        var groupId = await harness.SeedGroupAsync("G_FILTER");
        await harness.AddMembershipAsync(groupId, SupervisorId);
        await harness.AddMembershipAsync(groupId, peer1);
        await harness.AddMembershipAsync(groupId, peer2);

        var dossierId = await harness.SeedDossierAsync();
        await harness.SeedTaskAsync(dossierId, assignedUserId: peer1, title: "Match me");
        await harness.SeedTaskAsync(dossierId, assignedUserId: peer2, title: "Filtered out");

        // The harness's stubbed SqidService maps "SQID-{n}" to long n on decode.
        var result = await harness.Service.ListTeamQueueAsync(
            assigneeFilterSqid: $"SQID-{peer1}", page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().Title.Should().Be("Match me");
    }

    [Fact]
    public async Task ListTeamQueueAsync_ExcludesCompletedAndCancelledTasks()
    {
        var harness = await TestHarness.CreateAsync();
        var peerId = 5001L;
        await harness.SeedUserAsync(peerId);
        var groupId = await harness.SeedGroupAsync("G_TERMINAL");
        await harness.AddMembershipAsync(groupId, SupervisorId);
        await harness.AddMembershipAsync(groupId, peerId);

        var dossierId = await harness.SeedDossierAsync();
        await harness.SeedTaskAsync(dossierId, assignedUserId: peerId,
            title: "Pending OK", status: WorkflowTaskStatus.Pending);
        await harness.SeedTaskAsync(dossierId, assignedUserId: peerId,
            title: "Done — hide", status: WorkflowTaskStatus.Completed);
        await harness.SeedTaskAsync(dossierId, assignedUserId: peerId,
            title: "Cancelled — hide", status: WorkflowTaskStatus.Cancelled);

        var result = await harness.Service.ListTeamQueueAsync(
            assigneeFilterSqid: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().Title.Should().Be("Pending OK");
    }

    [Fact]
    public async Task ReassignTaskAsync_HappyPath_DelegatesToReassignAndEmitsMetric()
    {
        var harness = await TestHarness.CreateAsync();
        await harness.SeedUserAsync(6001L, displayName: "Target");
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: 6000L,
            status: WorkflowTaskStatus.InProgress);

        // The harness's stub Sqid service round-trips "SQID-{n}" ↔ n.
        var result = await harness.Service.ReassignTaskAsync(
            taskSqid: $"SQID-{task.Id}",
            newAssigneeSqid: "SQID-6001",
            reason: "Concediu medical");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(6001L);
        reloaded.ReassignmentReason.Should().Be("Concediu medical");
        // The audit chain must fire via the underlying ReassignAsync.
        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOWTASK.REASSIGNED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(WorkflowTask),
            task.Id,
            Arg.Is<string>(j => j.Contains("Concediu medical", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReassignTaskAsync_TargetNotFound_ReturnsNotFound()
    {
        var harness = await TestHarness.CreateAsync();
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: 7000L,
            status: WorkflowTaskStatus.InProgress);

        // 9999 is not seeded as a user — the underlying ReassignAsync must reject.
        var result = await harness.Service.ReassignTaskAsync(
            taskSqid: $"SQID-{task.Id}",
            newAssigneeSqid: "SQID-9999",
            reason: "Test reason");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ReassignTaskAsync_ReasonTooShort_ReturnsValidationFailed()
    {
        var harness = await TestHarness.CreateAsync();
        await harness.SeedUserAsync(8001L);
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: 8000L,
            status: WorkflowTaskStatus.InProgress);

        var result = await harness.Service.ReassignTaskAsync(
            taskSqid: $"SQID-{task.Id}",
            newAssigneeSqid: "SQID-8001",
            reason: "x"); // < 3 chars

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        // The row must NOT have been mutated.
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(8000L);
    }

    // ────────────────────────── Test harness ──────────────────────────

    /// <summary>Deterministic clock used by the harness.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its EF + NSubstitute collaborators.</summary>
    private sealed class TestHarness
    {
        /// <summary>Live EF Core InMemory context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>Service-under-test.</summary>
        public required TaskInboxService Service { get; init; }

        /// <summary>NSubstitute audit collaborator (verified by call sites).</summary>
        public required IAuditService Audit { get; init; }

        /// <summary>NSubstitute workflow-notification collaborator.</summary>
        public required IWorkflowNotificationOrchestrator Notifications { get; init; }

        /// <summary>Builds a fresh harness with the supervisor anchored to id 1000.</summary>
        public static async Task<TestHarness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-supervisor-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            // Stub Sqid service — round-trips "SQID-{n}" ↔ long n so tests can compose
            // Sqid filters without dragging in the real SqidService.
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string>()).Returns(call =>
            {
                var s = call.Arg<string>();
                if (s.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(s.AsSpan("SQID-".Length), out var v))
                {
                    return Result<long>.Success(v);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, $"bad sqid {s}");
            });

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(SupervisorId);
            caller.UserSqid.Returns($"SQID-{SupervisorId}");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-supervisor");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());

            var notify = Substitute.For<IWorkflowNotificationOrchestrator>();
            notify.DispatchAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());

            // Seed the supervisor profile so the audit user_id lookups don't 404.
            db.UserProfiles.Add(new UserProfile
            {
                Id = SupervisorId,
                CreatedAtUtc = ClockNow,
                DisplayName = "Test Supervisor",
                State = UserAccountState.Active,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var service = new TaskInboxService(db, sqids, new StubClock(ClockNow), caller, audit, notify);
            return new TestHarness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Notifications = notify,
            };
        }

        /// <summary>Seeds a UserProfile row with the supplied id.</summary>
        /// <param name="id">Explicit primary key (EF InMemory accepts it).</param>
        /// <param name="displayName">Optional display name; defaults to <c>User {id}</c>.</param>
        public async Task SeedUserAsync(long id, string? displayName = null)
        {
            Db.UserProfiles.Add(new UserProfile
            {
                Id = id,
                CreatedAtUtc = ClockNow,
                DisplayName = displayName ?? $"User {id}",
                State = UserAccountState.Active,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        /// <summary>Seeds a user-group row with the supplied code and returns its id.</summary>
        /// <param name="code">Stable domain code (e.g. <c>G_TEAM_A</c>).</param>
        /// <returns>The new group's surrogate id.</returns>
        public async Task<long> SeedGroupAsync(string code)
        {
            var group = new UserGroup
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                DisplayName = code,
                Kind = UserGroupKind.Custom,
                Status = UserGroupStatus.Active,
                IsActive = true,
            };
            Db.UserGroups.Add(group);
            await Db.SaveChangesAsync();
            return group.Id;
        }

        /// <summary>Adds a direct user-to-group membership row.</summary>
        /// <param name="groupId">Internal group id.</param>
        /// <param name="userId">Internal user-profile id.</param>
        public async Task AddMembershipAsync(long groupId, long userId)
        {
            Db.UserGroupMemberships.Add(new UserGroupMembership
            {
                CreatedAtUtc = ClockNow,
                UserGroupId = groupId,
                UserProfileId = userId,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// Seeds a minimal Solicitant → ServicePassport → Application → Dossier graph
        /// and returns the dossier id (mirrors the helper in
        /// <c>TaskInboxServiceTests</c> / <c>TaskInboxServiceReassignTests</c>).
        /// </summary>
        public async Task<long> SeedDossierAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000010",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Supervisor Test Solicitant",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = $"SP-SUP-{Guid.NewGuid():N}".Substring(0, 16),
                NameRo = "Supervisor test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-SUP-TEST",
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
                SnapshotJson = "{}",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-SUP-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>Inserts a <see cref="WorkflowTask"/> on the supplied dossier.</summary>
        /// <param name="dossierId">Parent dossier id.</param>
        /// <param name="assignedUserId">Initial assignee (may be null for a group inbox row).</param>
        /// <param name="title">Display title for assertions.</param>
        /// <param name="status">Initial status; defaults to <see cref="WorkflowTaskStatus.Pending"/>.</param>
        /// <returns>The persisted task entity (with id populated).</returns>
        public async Task<WorkflowTask> SeedTaskAsync(
            long dossierId,
            long? assignedUserId,
            string title = "Examinare dosar",
            WorkflowTaskStatus status = WorkflowTaskStatus.Pending)
        {
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossierId,
                Title = title,
                Status = status,
                AssignedUserId = assignedUserId,
                GroupCode = "cnas-examiner",
                DueAtUtc = ClockNow.AddDays(3),
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync();
            return task;
        }
    }
}
