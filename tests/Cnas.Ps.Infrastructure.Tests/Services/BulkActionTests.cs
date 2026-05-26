using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.BulkActions.Operations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — service-level tests for the bulk-action stack
/// (<see cref="BulkSelectionService"/>, <see cref="BulkOperationRunner"/>,
/// <see cref="BulkOperationRegistry"/>, and the sample
/// <see cref="WorkflowTaskReassignBulkOperation"/>).
/// </summary>
public class BulkActionTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── BulkSelectionService.CreateAsync ───────────────────────

    [Fact]
    public async Task CreateAsync_ResolvesFilter_AndPersistsIncludeExclude()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 3);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var include = new List<long> { 9999 };
        var exclude = new List<long> { 1 };

        var result = await harness.Selections.CreateAsync(
            BulkRegistries.WorkflowTask, filter, include, exclude);

        result.IsSuccess.Should().BeTrue();
        var row = await db.BulkSelections.SingleAsync();
        row.ExplicitIncludeIds.Should().BeEquivalentTo(include);
        row.ExplicitExcludeIds.Should().BeEquivalentTo(exclude);
        // 3 seeded Pending tasks (ids 1,2,3) + include 9999 - exclude 1 = 3 rows.
        row.ResolvedCount.Should().Be(3);
    }

    [Fact]
    public async Task ResolveIdsAsync_ReResolvesAgainstLiveDb_AfterRowsChange()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 2);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var sqid = create.Value.Id;
        var decoded = harness.Sqids.TryDecode(sqid);
        decoded.IsSuccess.Should().BeTrue();

        // Mutate the world between create and resolve — flip task 1 to InProgress.
        var task1 = await db.WorkflowTasks.SingleAsync(t => t.Id == 1);
        task1.Status = WorkflowTaskStatus.InProgress;
        await db.SaveChangesAsync();

        var resolved = await harness.Selections.ResolveIdsAsync(decoded.Value);

        resolved.IsSuccess.Should().BeTrue();
        // Only task 2 still matches the Pending filter.
        resolved.Value.Should().BeEquivalentTo(ExpectedSingleTaskTwo);
    }

    /// <summary>Static array referenced by the live-resolve test to satisfy CA1861.</summary>
    private static readonly long[] ExpectedSingleTaskTwo = { 2L };

    /// <summary>
    /// iter-149 — Fix 9: ResolveIdsAsync rejects an attempt to resolve another
    /// user's selection with Forbidden. Defence-in-depth at the service
    /// boundary even though BulkOperationRunner also performs the ownership
    /// check in its pre-loop block.
    /// </summary>
    [Fact]
    public async Task ResolveIdsAsync_OtherUsersSelection_ReturnsForbidden()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 2);

        // Owner creates the selection.
        var ownerHarness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await ownerHarness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var decoded = ownerHarness.Sqids.TryDecode(create.Value.Id);
        decoded.IsSuccess.Should().BeTrue();

        // A different user (OtherUserId) attempts to resolve.
        var otherHarness = ownerHarness.WithCaller(Harness.OtherUserId, "SQID-OTHER");

        var resolved = await otherHarness.Selections.ResolveIdsAsync(decoded.Value);

        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>Static array referenced by the malformed-sqid validator test to satisfy CA1861.</summary>
    private static readonly string[] MalformedSqidInclude = { "OK123", "bad!!" };

    // ─────────────────────── BulkOperationRunner refusal paths ───────────────────────

    [Fact]
    public async Task RunAsync_RefusesExpiredSelection()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 1);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();

        // Fast-forward the clock past the selection's expiry.
        harness.AdvanceClock(TimeSpan.FromHours(2));

        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);

        run.IsFailure.Should().BeTrue();
        run.ErrorCode.Should().Be(ErrorCodes.BulkSelectionExpired);
    }

    [Fact]
    public async Task RunAsync_RefusesSelectionOwnedByOtherUser()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 1);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();

        // Switch caller to a different user; reuse the same DbContext / clock.
        var other = harness.WithCaller(Harness.OtherUserId, "SQID-OTHER");

        var run = await other.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);

        run.IsFailure.Should().BeTrue();
        run.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task RunAsync_RefusesQuotaExceeded()
    {
        await using var db = CreateContext();
        // Seed more tasks than the per-op cap (1 000 by default).
        SeedWorkflowTasks(db, count: 5);

        // Build a registry whose sample operation has a tighter cap (2).
        var harness = Harness.Create(db, opOverride: new TightCapReassignOperation(db));

        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();

        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            TightCapReassignOperation.Code_,
            ParametersFor("SQID-100"),
            idempotencyKey: null);

        run.IsFailure.Should().BeTrue();
        run.ErrorCode.Should().Be(ErrorCodes.BulkQuotaExceeded);
    }

    // ─────────────────────── Idempotency ───────────────────────

    [Fact]
    public async Task RunAsync_IdempotencyKey_ReturnsPriorRunWithoutReexecuting()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 2);
        SeedUser(db, id: 100);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { Status = "Pending" });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();

        var first = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: "key-A");
        first.IsSuccess.Should().BeTrue();
        first.Value.SucceededRows.Should().Be(2);

        // A second call with the same key returns the prior run verbatim. We don't
        // even need a live selection because the runner short-circuits before
        // touching the row.
        var second = await harness.Runner.RunAsync(
            bulkSelectionId: 99999, // intentionally bogus — short-circuit must apply
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: "key-A");
        second.IsSuccess.Should().BeTrue();
        second.Value.Id.Should().Be(first.Value.Id);

        // Exactly one run row exists in the DB.
        (await db.BulkOperationRuns.CountAsync()).Should().Be(1);
    }

    // ─────────────────────── Per-row outcomes ───────────────────────

    [Fact]
    public async Task RunAsync_PerRowFailure_ProducesPartiallyFailed()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 3);
        // Flip task 2 to Completed so the operation refuses it (ALREADY_COMPLETED).
        var t2 = await db.WorkflowTasks.SingleAsync(t => t.Id == 2);
        t2.Status = WorkflowTaskStatus.Completed;
        await db.SaveChangesAsync();
        SeedUser(db, id: 100);

        var harness = Harness.Create(db);
        // Filter without a Status constraint so the Completed row is included.
        var filter = JsonSerializer.Serialize(new { });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);

        run.IsSuccess.Should().BeTrue();
        run.Value.Status.Should().Be(nameof(BulkOperationStatus.PartiallyFailed));
        run.Value.SucceededRows.Should().Be(2);
        run.Value.FailedRows.Should().Be(1);
        run.Value.FailureSummaryJson.Should().NotBeNull();
        run.Value.FailureSummaryJson!.Should().Contain("ALREADY_COMPLETED");
        run.Value.FailureSummaryJson.Should().Contain("SQID-2");
    }

    [Fact]
    public async Task RunAsync_AllRowsFail_ProducesFailed()
    {
        await using var db = CreateContext();
        // Two Completed tasks — every reassign attempt will refuse.
        SeedWorkflowTasks(db, count: 2, status: WorkflowTaskStatus.Completed);
        SeedUser(db, id: 100);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);

        run.IsSuccess.Should().BeTrue();
        run.Value.Status.Should().Be(nameof(BulkOperationStatus.Failed));
        run.Value.SucceededRows.Should().Be(0);
        run.Value.FailedRows.Should().Be(2);
    }

    // ─────────────────────── Audit emission ───────────────────────

    [Fact]
    public async Task RunAsync_EmitsStartedAndCompletedAuditRows()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 1);
        SeedUser(db, id: 100);

        var auditEvents = new List<string>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(code => auditEvents.Add(code)),
                Arg.Any<AuditSeverity>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var harness = Harness.Create(db, audit: audit);
        var filter = JsonSerializer.Serialize(new { });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);
        run.IsSuccess.Should().BeTrue();

        auditEvents.Should().Contain("BULK.WorkflowTask.Reassign.STARTED");
        auditEvents.Should().Contain("BULK.WorkflowTask.Reassign.COMPLETED");
    }

    // ─────────────────────── WorkflowTaskReassign happy path ───────────────────────

    [Fact]
    public async Task WorkflowTaskReassign_HappyPath_UpdatesAssignee_AndEmitsAudit()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 1);
        SeedUser(db, id: 100);

        var auditCodes = new List<string>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(code => auditCodes.Add(code)),
                Arg.Any<AuditSeverity>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var harness = Harness.Create(db, audit: audit);
        var filter = JsonSerializer.Serialize(new { });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();
        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            WorkflowTaskReassignBulkOperation.OperationCode,
            ParametersFor("SQID-100"),
            idempotencyKey: null);
        run.IsSuccess.Should().BeTrue();
        run.Value.Status.Should().Be(nameof(BulkOperationStatus.Completed));
        run.Value.SucceededRows.Should().Be(1);

        var task = await db.WorkflowTasks.SingleAsync();
        task.AssignedUserId.Should().Be(100);
        auditCodes.Should().Contain("WORKFLOWTASK.REASSIGNED");
    }

    // ─────────────────────── Registry / validator failures ───────────────────────

    [Fact]
    public async Task RunAsync_UnknownOperationCode_ReturnsBulkOpUnknown()
    {
        await using var db = CreateContext();
        SeedWorkflowTasks(db, count: 1);

        var harness = Harness.Create(db);
        var filter = JsonSerializer.Serialize(new { });
        var create = await harness.Selections.CreateAsync(BulkRegistries.WorkflowTask, filter, null, null);
        create.IsSuccess.Should().BeTrue();

        var run = await harness.Runner.RunAsync(
            DecodeSqid(harness, create.Value.Id),
            operationCode: "Nonsense.OpCode",
            parametersJson: null,
            idempotencyKey: null);

        run.IsFailure.Should().BeTrue();
        run.ErrorCode.Should().Be(ErrorCodes.BulkOperationUnknown);
    }

    [Fact]
    public void Validator_RejectsUnknownRegistry()
    {
        var v = new Cnas.Ps.Application.Validators.BulkSelectionCreateDtoValidator();
        var dto = new Cnas.Ps.Contracts.BulkSelectionCreateDto(
            Registry: "Foo",
            FilterJson: "{}",
            ExplicitIncludeIds: null,
            ExplicitExcludeIds: null);
        var result = v.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(dto.Registry));
    }

    [Fact]
    public void Validator_RejectsMalformedSqidInExplicitInclude()
    {
        var v = new Cnas.Ps.Application.Validators.BulkSelectionCreateDtoValidator();
        var dto = new Cnas.Ps.Contracts.BulkSelectionCreateDto(
            Registry: BulkRegistries.WorkflowTask,
            FilterJson: "{}",
            ExplicitIncludeIds: MalformedSqidInclude,
            ExplicitExcludeIds: null);
        var result = v.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("ExplicitIncludeIds"));
    }

    // ─────────────────────── Cleanup job ───────────────────────

    [Fact]
    public async Task BulkSelectionCleanupJob_DeletesRowsPastGraceWindow()
    {
        await using var db = CreateContext();

        // Insert two selections: one expired far past the grace window, one fresh.
        var old = new BulkSelection
        {
            Registry = BulkRegistries.WorkflowTask,
            OwnerUserId = Harness.UserId,
            FilterJson = "{}",
            ExpiresAtUtc = ClockNow - TimeSpan.FromDays(30),
            CreatedAtUtc = ClockNow - TimeSpan.FromDays(30),
            IsActive = true,
        };
        var fresh = new BulkSelection
        {
            Registry = BulkRegistries.WorkflowTask,
            OwnerUserId = Harness.UserId,
            FilterJson = "{}",
            ExpiresAtUtc = ClockNow + TimeSpan.FromHours(1),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.BulkSelections.Add(old);
        db.BulkSelections.Add(fresh);
        await db.SaveChangesAsync();

        var clock = new StubClock(ClockNow);
        var opts = Options.Create(new BulkSelectionOptions { CleanupGraceDays = 7 });
        // Scope factory that yields the in-memory DbContext as the scoped ICnasDbContext.
        var scopeFactory = new SingletonScopeFactory(db);

        var job = new Cnas.Ps.Infrastructure.Jobs.BulkSelectionCleanupJob(
            scopeFactory, clock,
            new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
            opts, NullLogger<Cnas.Ps.Infrastructure.Jobs.BulkSelectionCleanupJob>.Instance);

        var ctx = Substitute.For<Quartz.IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);

        await job.Execute(ctx);

        var remaining = await db.BulkSelections.ToListAsync();
        remaining.Should().ContainSingle().Which.ExpiresAtUtc.Should().Be(fresh.ExpiresAtUtc);
    }

    // ─────────────────────── Harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-bulk-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Seeds <paramref name="count"/> workflow tasks with sequential ids 1..count.</summary>
    private static void SeedWorkflowTasks(
        CnasDbContext db,
        int count,
        WorkflowTaskStatus status = WorkflowTaskStatus.Pending)
    {
        for (var i = 1; i <= count; i++)
        {
            db.WorkflowTasks.Add(new WorkflowTask
            {
                Id = i,
                Title = $"Task {i}",
                Status = status,
                DossierId = 1,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
        }
        db.SaveChanges();
    }

    /// <summary>Seeds a UserProfile row with the supplied id so the assignee-exists check passes.</summary>
    private static void SeedUser(CnasDbContext db, long id)
    {
        db.UserProfiles.Add(new UserProfile
        {
            Id = id,
            Email = $"user-{id}@cnas.md",
            DisplayName = $"Stub User {id}",
            IsActive = true,
            CreatedAtUtc = ClockNow,
        });
        db.SaveChanges();
    }

    /// <summary>Decodes a Sqid using the harness's Sqid stub; intentionally throws on failure.</summary>
    private static long DecodeSqid(Harness h, string sqid) => h.Sqids.TryDecode(sqid).Value;

    /// <summary>Serialises a <c>{"newAssigneeSqid":…}</c> body.</summary>
    private static string ParametersFor(string sqid) =>
        JsonSerializer.Serialize(new { newAssigneeSqid = sqid });

    /// <summary>Deterministic clock that supports an explicit Advance.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public StubClock(DateTime initial) => UtcNow = initial;
        public DateTime UtcNow { get; set; }
        public void Advance(TimeSpan delta) => UtcNow = UtcNow + delta;
    }

    /// <summary>Minimal scope factory that returns a scope yielding the supplied DbContext.</summary>
    private sealed class SingletonScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        private readonly CnasDbContext _db;
        public SingletonScopeFactory(CnasDbContext db) => _db = db;
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new Scope(_db);
        private sealed class Scope : Microsoft.Extensions.DependencyInjection.IServiceScope, IServiceProvider
        {
            private readonly CnasDbContext _db;
            public Scope(CnasDbContext db) => _db = db;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ICnasDbContext)) return _db;
                return null;
            }
            public void Dispose() { }
        }
    }

    /// <summary>Tighter-cap clone of the reassign operation used by the quota-exceeded test.</summary>
    private sealed class TightCapReassignOperation : IBulkOperation
    {
        public const string Code_ = "WorkflowTask.TightCap";
        public TightCapReassignOperation(CnasDbContext db) { _ = db; }
        public string Code => Code_;
        public string Registry => BulkRegistries.WorkflowTask;
        public string RequiredPermission => "WorkflowTask.Manage";
        public int MaxRowsPerRun => 2;
        public bool RequiresParameters => false;
        public Task<BulkRowOutcome> ExecuteAsync(long rowId, string? parametersJson, ICallerContext caller, CancellationToken ct) =>
            Task.FromResult(BulkRowOutcome.Succeeded());
    }

    private sealed class Harness
    {
        public const long UserId = 1001L;
        public const long OtherUserId = 1002L;

        public required CnasDbContext Db { get; init; }
        public required IBulkSelectionService Selections { get; init; }
        public required IBulkOperationRunner Runner { get; init; }
        public required ISqidService Sqids { get; init; }
        public required StubClock Clock { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }
        public required IBulkOperation Operation { get; init; }

        public void AdvanceClock(TimeSpan delta) => Clock.Advance(delta);

        public Harness WithCaller(long userId, string userSqid)
        {
            var newCaller = BuildCaller(userId, userSqid);
            // Reuse the same DB / sqids / clock so the second harness sees the same world.
            return Build(Db, newCaller, Sqids, Clock, Audit, Operation);
        }

        public static Harness Create(
            CnasDbContext db,
            IAuditService? audit = null,
            IBulkOperation? opOverride = null)
        {
            var clock = new StubClock(ClockNow);
            var sqids = BuildSqids();
            var caller = BuildCaller(UserId, "SQID-OWNER");
            var resolvedAudit = audit ?? BuildAuditStub();
            var operation = opOverride
                ?? new WorkflowTaskReassignBulkOperation(db, sqids, resolvedAudit, clock);
            return Build(db, caller, sqids, clock, resolvedAudit, operation);
        }

        private static Harness Build(
            CnasDbContext db,
            ICallerContext caller,
            ISqidService sqids,
            StubClock clock,
            IAuditService audit,
            IBulkOperation operation)
        {
            // R0671 follow-up — the three list-shaped resolvers now consume
            // IAccessScopeFilter + ICallerContext so a scoped caller never receives
            // ids of rows outside their allow-list. The harness wires the production
            // filter implementation + reuses the caller stub built above (whose
            // AccessScope returns RolesBasedAccessScope.Unscoped) so existing tests
            // observe no behavioural change.
            var accessFilter = new Cnas.Ps.Infrastructure.AccessScope.AccessScopeFilter();
            var resolverFactory = new BulkSelectionFilterResolverFactory(new IBulkSelectionFilterResolver[]
            {
                new SolicitantFilterResolver(db),
                new CerereFilterResolver(db, accessFilter, caller),
                new WorkflowTaskFilterResolver(db, sqids, accessFilter, caller),
                new DecisionFilterResolver(db, accessFilter, caller),
            });

            var selectionOpts = Options.Create(new BulkSelectionOptions());
            var selections = new BulkSelectionService(db, caller, sqids, clock, resolverFactory, selectionOpts);

            var registry = new BulkOperationRegistry(new[] { operation });
            var runnerOpts = Options.Create(new BulkOperationOptions { MaxRowsPerRun = 5_000, MaxFailureSummaryEntries = 100 });
            var runner = new BulkOperationRunner(
                db, caller, sqids, clock, selections, registry, audit, runnerOpts,
                NullLogger<BulkOperationRunner>.Instance);

            return new Harness
            {
                Db = db,
                Selections = selections,
                Runner = runner,
                Sqids = sqids,
                Clock = clock,
                Audit = audit,
                Caller = caller,
                Operation = operation,
            };
        }

        private static ISqidService BuildSqids()
        {
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var arg = call.Arg<string?>();
                if (!string.IsNullOrEmpty(arg)
                    && arg.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(arg.AsSpan(5), out var n))
                {
                    return Result<long>.Success(n);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });
            return sqids;
        }

        private static ICallerContext BuildCaller(long userId, string userSqid)
        {
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(userId);
            caller.UserSqid.Returns(userSqid);
            caller.Roles.Returns(["cnas-user"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{userId}");
            // R0671 — IAccessScope is contract-non-null; the substitute would otherwise
            // hand back a null IAccessScope which violates the consumer expectation.
            caller.AccessScope.Returns(RolesBasedAccessScope.Unscoped);
            return caller;
        }

        private static IAuditService BuildAuditStub()
        {
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            return audit;
        }
    }
}
