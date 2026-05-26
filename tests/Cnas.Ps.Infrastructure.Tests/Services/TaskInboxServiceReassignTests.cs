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

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0127 / CF 16.11 — integration tests for the per-task reassignment surface on
/// <see cref="TaskInboxService"/>. Each test exercises the SUT against an EF Core
/// InMemory backend and verifies row mutation + audit emission + Result shape.
/// </summary>
public sealed class TaskInboxServiceReassignTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReassignAsync_HappyPath_MovesTaskAndCapturesOriginalAssignee()
    {
        // Arrange — task currently assigned to user 100; reassign to user 200.
        var harness = await TestHarness.CreateAsync(ClockNow);
        var task = await harness.SeedTaskAsync(assignedUserId: 100L, status: WorkflowTaskStatus.InProgress);
        await harness.SeedUserAsync(200L, UserAccountState.Active);

        // Act
        var result = await harness.Service.ReassignAsync(
            task.Id, newAssigneeUserId: 200L, reason: "Concediu medical", absenceId: null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(200L);
        reloaded.OriginalAssigneeUserId.Should().Be(100L);
        reloaded.ReassignmentCount.Should().Be(1);
        reloaded.ReassignmentReason.Should().Be("Concediu medical");

        // Audit emitted
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
    public async Task ReassignAsync_SecondReassignment_DoesNotOverwriteOriginal()
    {
        // Arrange — task already once-delegated from user 100 to user 200.
        var harness = await TestHarness.CreateAsync(ClockNow);
        await harness.SeedUserAsync(200L, UserAccountState.Active);
        await harness.SeedUserAsync(300L, UserAccountState.Active);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 200L,
            status: WorkflowTaskStatus.InProgress,
            originalAssigneeUserId: 100L,
            reassignmentCount: 1);

        // Act — reassign a second time to user 300.
        var result = await harness.Service.ReassignAsync(
            task.Id, newAssigneeUserId: 300L, reason: "Concediu de odihnă", absenceId: null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(300L);
        // OriginalAssigneeUserId must STILL be the very first owner.
        reloaded.OriginalAssigneeUserId.Should().Be(100L);
        reloaded.ReassignmentCount.Should().Be(2);
    }

    [Fact]
    public async Task ReassignAsync_TerminalStatus_ReturnsValidationFailed()
    {
        var harness = await TestHarness.CreateAsync(ClockNow);
        await harness.SeedUserAsync(200L, UserAccountState.Active);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 100L,
            status: WorkflowTaskStatus.Completed);

        var result = await harness.Service.ReassignAsync(
            task.Id, newAssigneeUserId: 200L, reason: "Late", absenceId: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ReassignAsync_NewAssigneeSuspended_ReturnsForbidden()
    {
        var harness = await TestHarness.CreateAsync(ClockNow);
        await harness.SeedUserAsync(200L, UserAccountState.Suspended);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 100L,
            status: WorkflowTaskStatus.InProgress);

        var result = await harness.Service.ReassignAsync(
            task.Id, newAssigneeUserId: 200L, reason: "Test", absenceId: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task ReassignAsync_ResetsUnclaimedSinceUtcToClockNow()
    {
        // R0202 integration — the delegate gets a fresh SLA clock.
        var harness = await TestHarness.CreateAsync(ClockNow);
        await harness.SeedUserAsync(200L, UserAccountState.Active);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 100L,
            status: WorkflowTaskStatus.InProgress,
            unclaimedSinceUtc: ClockNow.AddDays(-5)); // stale stamp from days ago.

        var result = await harness.Service.ReassignAsync(
            task.Id, newAssigneeUserId: 200L, reason: "Reset SLA", absenceId: null);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.UnclaimedSinceUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task RevertReassignmentAsync_HappyPath_RestoresOriginalAndEmitsAudit()
    {
        var harness = await TestHarness.CreateAsync(ClockNow);
        await harness.SeedUserAsync(200L, UserAccountState.Active);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 200L,
            status: WorkflowTaskStatus.InProgress,
            originalAssigneeUserId: 100L,
            delegatedFromAbsenceId: 7L,
            reassignmentCount: 1);

        var result = await harness.Service.RevertReassignmentAsync(task.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(100L);
        reloaded.DelegatedFromAbsenceId.Should().BeNull();
        reloaded.ReassignmentCount.Should().Be(2); // bumped on revert

        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOWTASK.REASSIGNMENT_REVERTED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(WorkflowTask),
            task.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevertReassignmentAsync_NeverReassigned_ReturnsValidationFailed()
    {
        var harness = await TestHarness.CreateAsync(ClockNow);
        var task = await harness.SeedTaskAsync(
            assignedUserId: 100L,
            status: WorkflowTaskStatus.InProgress);

        var result = await harness.Service.RevertReassignmentAsync(task.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ────────────────────────── Test harness ──────────────────────────

    /// <summary>Stub clock used by the harness.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT with its EF + NSubstitute collaborators.</summary>
    private sealed class TestHarness
    {
        public required CnasDbContext Db { get; init; }
        public required TaskInboxService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required IWorkflowNotificationOrchestrator Notifications { get; init; }

        public static async Task<TestHarness> CreateAsync(DateTime now)
        {
            await Task.Yield();
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reassign-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1L);
            caller.UserSqid.Returns("SQID-1");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

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

            var service = new TaskInboxService(db, sqids, new StubClock(now), caller, audit, notify);
            return new TestHarness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Notifications = notify,
            };
        }

        public async Task<WorkflowTask> SeedTaskAsync(
            long? assignedUserId,
            WorkflowTaskStatus status,
            DateTime? unclaimedSinceUtc = null,
            long? originalAssigneeUserId = null,
            long? delegatedFromAbsenceId = null,
            int reassignmentCount = 0)
        {
            // Minimal dossier graph.
            var dossierId = await SeedDossierAsync().ConfigureAwait(false);
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossierId,
                Title = "Examinare cerere",
                Status = status,
                AssignedUserId = assignedUserId,
                GroupCode = "cnas-examiner",
                UnclaimedSinceUtc = unclaimedSinceUtc,
                OriginalAssigneeUserId = originalAssigneeUserId,
                DelegatedFromAbsenceId = delegatedFromAbsenceId,
                ReassignmentCount = reassignmentCount,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync().ConfigureAwait(false);
            return task;
        }

        public async Task SeedUserAsync(long id, UserAccountState state)
        {
            var user = new UserProfile
            {
                Id = id, // EF InMemory accepts explicit ids
                CreatedAtUtc = ClockNow,
                DisplayName = $"User {id}",
                State = state,
                IsActive = true,
            };
            Db.UserProfiles.Add(user);
            await Db.SaveChangesAsync().ConfigureAwait(false);
        }

        private async Task<long> SeedDossierAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test Solicitant",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-RA",
                NameRo = "Test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync().ConfigureAwait(false);

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
            await Db.SaveChangesAsync().ConfigureAwait(false);

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid().ToString("N")[..8]}",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync().ConfigureAwait(false);
            return dossier.Id;
        }
    }
}
