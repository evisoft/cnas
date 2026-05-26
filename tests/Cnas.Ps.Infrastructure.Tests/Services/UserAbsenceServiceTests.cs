using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Application.WorkflowTasks;
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
/// R0127 / CF 16.11 — integration tests for <see cref="UserAbsenceService"/> covering
/// validator behaviour, lifecycle transitions, and the task routing / revert sweep.
/// </summary>
public sealed class UserAbsenceServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PlanAsync_RejectsBackdateBeyondSevenDays()
    {
        var h = await Harness.CreateAsync(ClockNow);
        await h.SeedUserAsync(100L);
        await h.SeedUserAsync(200L);

        var dto = new UserAbsenceCreateDto(
            UserSqid: "SQID-100",
            StartDateUtc: ClockNow.AddDays(-30),
            EndDateUtc: ClockNow.AddDays(5),
            DelegateSqid: "SQID-200",
            Reason: "Concediu medical");

        var result = await h.Service.PlanAsync(dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task PlanAsync_RejectsDelegateEqualToUser()
    {
        var h = await Harness.CreateAsync(ClockNow);
        await h.SeedUserAsync(100L);

        var dto = new UserAbsenceCreateDto(
            UserSqid: "SQID-100",
            StartDateUtc: ClockNow.AddDays(1),
            EndDateUtc: ClockNow.AddDays(5),
            DelegateSqid: "SQID-100",
            Reason: "Bad input");

        var result = await h.Service.PlanAsync(dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task PlanAsync_RejectsOverlappingPlannedRow()
    {
        var h = await Harness.CreateAsync(ClockNow);
        await h.SeedUserAsync(100L);
        await h.SeedUserAsync(200L);
        // Existing planned absence covering days [now+1 .. now+10].
        h.Db.UserAbsences.Add(new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(1),
            EndDateUtc = ClockNow.AddDays(10),
            Reason = "Existing",
            Status = UserAbsenceStatus.Planned,
            IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        // Try to plan another one overlapping days [now+5 .. now+15].
        var dto = new UserAbsenceCreateDto(
            UserSqid: "SQID-100",
            StartDateUtc: ClockNow.AddDays(5),
            EndDateUtc: ClockNow.AddDays(15),
            DelegateSqid: "SQID-200",
            Reason: "Overlap attempt");

        var result = await h.Service.PlanAsync(dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ActivateAsync_RoutesOpenTasksToDelegate_AndIncrementsRoutedCount()
    {
        var h = await Harness.CreateAsync(ClockNow);
        await h.SeedUserAsync(100L);
        await h.SeedUserAsync(200L);
        var task1 = await h.SeedTaskAsync(assignedUserId: 100L, status: WorkflowTaskStatus.Pending);
        var task2 = await h.SeedTaskAsync(assignedUserId: 100L, status: WorkflowTaskStatus.InProgress);
        // Unrelated open task — different user — must NOT be touched.
        var taskOther = await h.SeedTaskAsync(assignedUserId: 999L, status: WorkflowTaskStatus.Pending);

        // Seed a planned absence.
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(-1),
            EndDateUtc = ClockNow.AddDays(5),
            Reason = "Concediu medical",
            Status = UserAbsenceStatus.Planned,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ActivateAsync(absence.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await h.Db.UserAbsences.SingleAsync(a => a.Id == absence.Id);
        reloaded.Status.Should().Be(UserAbsenceStatus.Active);
        reloaded.RoutedTaskCount.Should().Be(2);
        reloaded.ActivatedAtUtc.Should().Be(ClockNow);

        var t1 = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == task1.Id);
        t1.AssignedUserId.Should().Be(200L);
        t1.DelegatedFromAbsenceId.Should().Be(absence.Id);
        t1.OriginalAssigneeUserId.Should().Be(100L);

        var t2 = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == task2.Id);
        t2.AssignedUserId.Should().Be(200L);

        var tOther = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == taskOther.Id);
        tOther.AssignedUserId.Should().Be(999L); // untouched
    }

    [Fact]
    public async Task CompleteAsync_RevertsOpenDelegatedTasksToOriginalAssignee()
    {
        var h = await Harness.CreateAsync(ClockNow);
        await h.SeedUserAsync(100L);
        await h.SeedUserAsync(200L);

        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(-5),
            EndDateUtc = ClockNow.AddDays(-1),
            Reason = "Concediu",
            Status = UserAbsenceStatus.Active,
            ActivatedAtUtc = ClockNow.AddDays(-5),
            RoutedTaskCount = 2,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        // Two delegated tasks — one open (must revert), one Completed (must NOT revert).
        var open = await h.SeedTaskAsync(
            assignedUserId: 200L,
            status: WorkflowTaskStatus.InProgress,
            originalAssigneeUserId: 100L,
            delegatedFromAbsenceId: absence.Id,
            reassignmentCount: 1);
        var done = await h.SeedTaskAsync(
            assignedUserId: 200L,
            status: WorkflowTaskStatus.Completed,
            originalAssigneeUserId: 100L,
            delegatedFromAbsenceId: absence.Id,
            reassignmentCount: 1);

        var result = await h.Service.CompleteAsync(absence.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await h.Db.UserAbsences.SingleAsync(a => a.Id == absence.Id);
        reloaded.Status.Should().Be(UserAbsenceStatus.Completed);
        reloaded.CompletedAtUtc.Should().Be(ClockNow);

        var openReloaded = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == open.Id);
        openReloaded.AssignedUserId.Should().Be(100L);
        openReloaded.DelegatedFromAbsenceId.Should().BeNull();

        var doneReloaded = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == done.Id);
        doneReloaded.AssignedUserId.Should().Be(200L); // unchanged
    }

    [Fact]
    public async Task CancelAsync_FlipsPlannedToCancelled()
    {
        var h = await Harness.CreateAsync(ClockNow);
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(2),
            EndDateUtc = ClockNow.AddDays(10),
            Reason = "Plan",
            Status = UserAbsenceStatus.Planned,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        var result = await h.Service.CancelAsync(absence.Id);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await h.Db.UserAbsences.SingleAsync(a => a.Id == absence.Id);
        reloaded.Status.Should().Be(UserAbsenceStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAsync_RejectsActiveAbsence()
    {
        var h = await Harness.CreateAsync(ClockNow);
        var absence = new UserAbsence
        {
            CreatedAtUtc = ClockNow,
            UserUserId = 100L,
            DelegateUserId = 200L,
            StartDateUtc = ClockNow.AddDays(-2),
            EndDateUtc = ClockNow.AddDays(2),
            Reason = "Active",
            Status = UserAbsenceStatus.Active,
            IsActive = true,
        };
        h.Db.UserAbsences.Add(absence);
        await h.Db.SaveChangesAsync();

        var result = await h.Service.CancelAsync(absence.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ──────────────────────────── Test harness ────────────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UserAbsenceService Service { get; init; }
        public required ITaskInboxService Tasks { get; init; }

        public static async Task<Harness> CreateAsync(DateTime now)
        {
            await Task.Yield();
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-absence-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            // Permissive decoder — extracts the suffix integer from SQID-XYZ.
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var s = call.Arg<string?>();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "Empty sqid.");
                }
                var dash = s.IndexOf('-');
                if (dash < 0 || !long.TryParse(s[(dash + 1)..], out var id))
                {
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "Cannot decode.");
                }
                return Result<long>.Success(id);
            });

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1L);
            caller.UserSqid.Returns("SQID-1");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var clock = new StubClock(now);

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

            // Use the REAL TaskInboxService so the routing path actually mutates tasks.
            var tasks = new TaskInboxService(db, sqids, clock, caller, audit, notify);
            var service = new UserAbsenceService(db, clock, sqids, caller, tasks, audit);

            return new Harness
            {
                Db = db,
                Service = service,
                Tasks = tasks,
            };
        }

        public async Task SeedUserAsync(long id)
        {
            Db.UserProfiles.Add(new UserProfile
            {
                Id = id,
                CreatedAtUtc = ClockNow,
                DisplayName = $"User {id}",
                State = UserAccountState.Active,
                IsActive = true,
            });
            await Db.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task<WorkflowTask> SeedTaskAsync(
            long? assignedUserId,
            WorkflowTaskStatus status,
            long? originalAssigneeUserId = null,
            long? delegatedFromAbsenceId = null,
            int reassignmentCount = 0)
        {
            var dossierId = await EnsureDossierAsync().ConfigureAwait(false);
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossierId,
                Title = "Task",
                Status = status,
                AssignedUserId = assignedUserId,
                OriginalAssigneeUserId = originalAssigneeUserId,
                DelegatedFromAbsenceId = delegatedFromAbsenceId,
                ReassignmentCount = reassignmentCount,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync().ConfigureAwait(false);
            return task;
        }

        private async Task<long> EnsureDossierAsync()
        {
            var existing = await Db.Dossiers.FirstOrDefaultAsync().ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.Id;
            }
            var sol = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "S",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(sol);
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP",
                NameRo = "N",
                DescriptionRo = "D",
                FormSchemaJson = "{}",
                WorkflowCode = "W",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync().ConfigureAwait(false);
            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = sol.Id,
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
