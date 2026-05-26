using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
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
/// Integration tests for <see cref="TaskInboxService"/> (UC05 — workflow inbox).
/// Uses EF Core InMemory for persistence and NSubstitute for collaborators (sqid, clock,
/// caller). Covers list / claim / complete paths plus the documented claim-race
/// limitation flagged in <c>UseCaseStubs.cs</c>.
/// </summary>
public class TaskInboxServiceTests
{
    /// <summary>Deterministic clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Internal database id of the simulated calling user across the suite.</summary>
    private const long CallerUserId = 42L;

    /// <summary>Internal database id of an unrelated user (used for "not mine" scenarios).</summary>
    private const long OtherUserId = 99L;

    /// <summary>Caller roles — value is irrelevant to TaskInboxService but required by the contract.</summary>
    private static readonly string[] CallerRoles = ["cnas-examiner"];

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task ListAsync_NoCaller_ReturnsUnauthorized()
    {
        // Anonymous (UserId == null) callers must be rejected with UNAUTHORIZED so the API
        // layer can map to HTTP 401 without leaking inbox row counts.
        var harness = Harness.Create(authenticated: false);

        var result = await harness.Service.ListAsync(new PageRequest(1, 10));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTasksAssignedToCallerOrderedByDueAt()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();

        // Three tasks: two for the caller (different due dates), one for someone else.
        await harness.SeedTaskAsync(dossierId, assignedUserId: CallerUserId, dueAtUtc: ClockNow.AddDays(5), title: "Later");
        await harness.SeedTaskAsync(dossierId, assignedUserId: CallerUserId, dueAtUtc: ClockNow.AddDays(1), title: "Sooner");
        await harness.SeedTaskAsync(dossierId, assignedUserId: OtherUserId, dueAtUtc: ClockNow.AddDays(2), title: "Not mine");

        var result = await harness.Service.ListAsync(new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().HaveCount(2);
        // Ordered by DueAtUtc ascending.
        result.Value.Items[0].Title.Should().Be("Sooner");
        result.Value.Items[1].Title.Should().Be("Later");
    }

    [Fact]
    public async Task ListAsync_RespectsPaging()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        for (var i = 0; i < 5; i++)
        {
            await harness.SeedTaskAsync(
                dossierId,
                assignedUserId: CallerUserId,
                dueAtUtc: ClockNow.AddDays(i + 1),
                title: $"Task #{i + 1}");
        }

        var result = await harness.Service.ListAsync(new PageRequest(Page: 2, PageSize: 2));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(5);
        result.Value.Items.Should().HaveCount(2);
        // Page 2 (skip 2) of an ascending-by-due list of 5 items → items #3 and #4.
        result.Value.Items[0].Title.Should().Be("Task #3");
        result.Value.Items[1].Title.Should().Be("Task #4");
    }

    // ─────────────────────── ClaimAsync ───────────────────────

    [Fact]
    public async Task ClaimAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.ClaimAsync("bad");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task ClaimAsync_TaskNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.ClaimAsync("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ClaimAsync_HappyPath_AssignsToCallerAndSetsInProgress()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        // Initially the task is unassigned (group inbox).
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: null, dueAtUtc: ClockNow.AddDays(3));
        harness.Sqids.TryDecode("TASK-SQID").Returns(Result<long>.Success(task.Id));

        var result = await harness.Service.ClaimAsync("TASK-SQID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(CallerUserId);
        reloaded.Status.Should().Be(WorkflowTaskStatus.InProgress);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task ClaimAsync_AlreadyAssignedToAnotherUser_ReturnsWorkflowNotAssignee()
    {
        // Deny-by-default (CLAUDE.md §5.4): a second caller MUST NOT be able to steal a task
        // that already belongs to another examiner. The service returns
        // WORKFLOW_NOT_ASSIGNEE — TasksController maps that to HTTP 403 — and leaves the
        // existing AssignedUserId untouched so the original claimer keeps the row.
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        // Task already owned by OtherUserId; caller (CallerUserId) tries to claim it.
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: OtherUserId, dueAtUtc: ClockNow.AddDays(3));
        harness.Sqids.TryDecode("TASK-SQID").Returns(Result<long>.Success(task.Id));
        var originalStatus = task.Status;
        var originalUpdatedAt = task.UpdatedAtUtc;

        var result = await harness.Service.ClaimAsync("TASK-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotAssignee);

        // The original assignee must still own the row — no silent overwrite.
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(OtherUserId,
            "the deny guard must not transfer ownership to the second caller");
        reloaded.Status.Should().Be(originalStatus, "the rejected claim must not advance status");
        reloaded.UpdatedAtUtc.Should().Be(originalUpdatedAt,
            "the rejected claim must not bump the audit timestamp");
    }

    [Fact]
    public async Task ClaimAsync_AlreadyAssignedToSameUser_IsIdempotent()
    {
        // Re-claim by the SAME caller is idempotent — useful for retry / refresh flows where
        // a UI may double-fire the claim button. The row stays assigned to the caller and
        // its Status flips to InProgress (preserving the original happy-path semantics).
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: CallerUserId, dueAtUtc: ClockNow.AddDays(3));
        harness.Sqids.TryDecode("TASK-SQID").Returns(Result<long>.Success(task.Id));

        var result = await harness.Service.ClaimAsync("TASK-SQID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(CallerUserId, "the same-caller re-claim must keep ownership");
        reloaded.Status.Should().Be(WorkflowTaskStatus.InProgress);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── CompleteAsync ───────────────────────

    [Fact]
    public async Task CompleteAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.CompleteAsync("bad", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task CompleteAsync_TaskNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.CompleteAsync("missing", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task CompleteAsync_NotAssignedToCaller_ReturnsWorkflowNotAssignee()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: OtherUserId, dueAtUtc: ClockNow.AddDays(3));
        harness.Sqids.TryDecode("TASK-SQID").Returns(Result<long>.Success(task.Id));

        var result = await harness.Service.CompleteAsync("TASK-SQID", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotAssignee);

        // Status must be unchanged — the wrong-assignee guard must not mutate the row.
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(WorkflowTaskStatus.Pending);
        reloaded.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_HappyPath_SetsCompletedStatusAndTimestamps()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync();
        var task = await harness.SeedTaskAsync(dossierId, assignedUserId: CallerUserId, dueAtUtc: ClockNow.AddDays(3));
        harness.Sqids.TryDecode("TASK-SQID").Returns(Result<long>.Success(task.Id));

        var result = await harness.Service.CompleteAsync("TASK-SQID", "{\"verdict\":\"ok\"}");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(WorkflowTaskStatus.Completed);
        reloaded.CompletedAtUtc.Should().Be(ClockNow);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-inbox-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required TaskInboxService Service { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        /// <summary>
        /// Wires the SUT with NSubstitute fakes and a fresh InMemory DB.
        /// </summary>
        /// <param name="authenticated">When false, the caller exposes a null UserId (anonymous).</param>
        public static Harness Create(bool authenticated = true)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns(authenticated ? "SQID-CALLER" : null);
            caller.UserId.Returns(authenticated ? CallerUserId : (long?)null);
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var service = new TaskInboxService(db, sqids, clock, caller);
            return new Harness
            {
                Db = db,
                Service = service,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        /// <summary>Seeds the minimal solicitant + passport + application + dossier graph and returns the dossier id.</summary>
        public async Task<long> SeedDossierAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
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
                SnapshotJson = "{}",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-2026-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>Inserts a <see cref="WorkflowTask"/> in <see cref="WorkflowTaskStatus.Pending"/>.</summary>
        public async Task<WorkflowTask> SeedTaskAsync(
            long dossierId,
            long? assignedUserId,
            DateTime? dueAtUtc,
            string title = "Examinare cerere")
        {
            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossierId,
                Title = title,
                Status = WorkflowTaskStatus.Pending,
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
