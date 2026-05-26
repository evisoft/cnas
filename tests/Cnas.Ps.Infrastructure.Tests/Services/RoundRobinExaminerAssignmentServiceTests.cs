using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ApplicationProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0570 / TOR CF 08.02 — integration tests for
/// <see cref="RoundRobinExaminerAssignmentService"/>. Pins the registrar
/// exclusion, uniform-spread rotation, empty-pool failure, and the
/// across-restart cursor persistence contracts.
/// </summary>
public sealed class RoundRobinExaminerAssignmentServiceTests
{
    /// <summary>Deterministic clock instant.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stable role claim recognised by the assignment service.</summary>
    private const string ExaminerRole = "cnas-examiner";

    /// <summary>
    /// R0570 — a single examiner in the pool gets every assignment regardless
    /// of which user submits.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_SingleExaminer_ReturnsThem()
    {
        var db = CreateContext();
        var registrar = await SeedUserAsync(db, displayName: "Registrar", isExaminer: false);
        var examiner = await SeedUserAsync(db, displayName: "Examiner", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        var result = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(examiner.Id);
    }

    /// <summary>
    /// R0570 — the registrar is excluded from the candidate pool even when
    /// they themselves carry the examiner role (CF 08.02: same person cannot
    /// register AND examine).
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_RegistrarHasExaminerRole_IsStillExcluded()
    {
        var db = CreateContext();
        var registrarAndExaminer = await SeedUserAsync(db, displayName: "Both", isExaminer: true);
        var pureExaminer = await SeedUserAsync(db, displayName: "Other", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        var result = await svc.AssignExaminerAsync(
            applicationId: 1, registrarUserId: registrarAndExaminer.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(pureExaminer.Id,
            "the registrar must never be assigned even when they have the role");
    }

    /// <summary>
    /// R0570 — consecutive calls fan out across the pool in canonical
    /// (Id-ASC) order, demonstrating the uniform-spread contract.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_MultiCall_RoundRobinOrder()
    {
        var db = CreateContext();
        var registrar = await SeedUserAsync(db, displayName: "Reg", isExaminer: false);
        var e1 = await SeedUserAsync(db, displayName: "E1", isExaminer: true);
        var e2 = await SeedUserAsync(db, displayName: "E2", isExaminer: true);
        var e3 = await SeedUserAsync(db, displayName: "E3", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        var r1 = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);
        var r2 = await svc.AssignExaminerAsync(applicationId: 2, registrarUserId: registrar.Id);
        var r3 = await svc.AssignExaminerAsync(applicationId: 3, registrarUserId: registrar.Id);
        var r4 = await svc.AssignExaminerAsync(applicationId: 4, registrarUserId: registrar.Id);

        r1.Value.Should().Be(e1.Id);
        r2.Value.Should().Be(e2.Id);
        r3.Value.Should().Be(e3.Id);
        r4.Value.Should().Be(e1.Id, "the round-robin must wrap back to the first examiner");
    }

    /// <summary>
    /// R0570 — when the ONLY examiner is also the registrar, the pool is
    /// empty and the service returns the canonical
    /// <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/> failure.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_OnlyRegistrarIsExaminer_ReturnsNoAvailableExaminer()
    {
        var db = CreateContext();
        var registrar = await SeedUserAsync(db, displayName: "Solo", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        var result = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationNoAvailableExaminer);
    }

    /// <summary>
    /// R0570 — inactive examiners (IsActive=false OR State != Active) are
    /// skipped so a deactivated staff account stops receiving work
    /// immediately.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_InactiveExaminersSkipped()
    {
        var db = CreateContext();
        var registrar = await SeedUserAsync(db, displayName: "Reg", isExaminer: false);
        var inactive = await SeedUserAsync(db, displayName: "Inactive", isExaminer: true);
        inactive.IsActive = false;
        var disabled = await SeedUserAsync(db, displayName: "Disabled", isExaminer: true);
        disabled.State = UserAccountState.Disabled;
        var active = await SeedUserAsync(db, displayName: "Active", isExaminer: true);
        await db.SaveChangesAsync();

        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));
        var result = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(active.Id, "only the IsActive + State.Active examiner should be picked");
    }

    /// <summary>
    /// R0570 — concurrency contract. A simulated
    /// <see cref="DbUpdateConcurrencyException"/> on the cursor save is
    /// absorbed by the bounded retry loop: the second attempt re-reads the
    /// cursor (cleared change-tracker) and succeeds. Pins the fix for the
    /// "two concurrent submissions kill the assignment" failure mode.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_TransientConcurrencyConflict_RetriesAndSucceeds()
    {
        var db = new TransientConcurrencyDbContext(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rr-assign-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            throwOnNextSaves: 1);
        var registrar = await SeedUserAsync(db, displayName: "Reg", isExaminer: false);
        var examiner = await SeedUserAsync(db, displayName: "Examiner", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        var result = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);

        result.IsSuccess.Should().BeTrue("the retry loop must absorb a single transient concurrency conflict");
        result.Value.Should().Be(examiner.Id);
    }

    /// <summary>
    /// R0570 — bounded retry contract. Three consecutive transient
    /// concurrency conflicts must surface as a structured Conflict failure
    /// (not bubble as an unhandled exception). Bounded contention protects
    /// the API surface from spinning under pathological storms.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_ExhaustedRetries_ReturnsConflict()
    {
        var db = new TransientConcurrencyDbContext(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rr-assign-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            throwOnNextSaves: 99); // Far exceeds the 3-attempt budget.
        var registrar = await SeedUserAsync(db, displayName: "Reg", isExaminer: false);
        await SeedUserAsync(db, displayName: "Examiner", isExaminer: true);
        var svc = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));

        Result<long> result = default;
        var act = async () => result = await svc.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);

        await act.Should().NotThrowAsync(
            "the service must surface exhausted contention as a Result, never an unhandled DbUpdateConcurrencyException");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// R0570 — the cursor row persists across service-instance restarts so
    /// the rotation does not reset to the first examiner on every deploy.
    /// </summary>
    [Fact]
    public async Task AssignExaminerAsync_PersistedCursor_SurvivesServiceRestart()
    {
        var db = CreateContext();
        var registrar = await SeedUserAsync(db, displayName: "Reg", isExaminer: false);
        var e1 = await SeedUserAsync(db, displayName: "E1", isExaminer: true);
        var e2 = await SeedUserAsync(db, displayName: "E2", isExaminer: true);

        // First service instance picks e1; the cursor is persisted.
        var svc1 = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));
        var r1 = await svc1.AssignExaminerAsync(applicationId: 1, registrarUserId: registrar.Id);
        r1.Value.Should().Be(e1.Id);

        // Fresh service instance against the SAME database — the persisted
        // cursor advances the rotation rather than restarting at zero.
        var svc2 = new RoundRobinExaminerAssignmentService(db, new StubClock(ClockNow));
        var r2 = await svc2.AssignExaminerAsync(applicationId: 2, registrarUserId: registrar.Id);
        r2.Value.Should().Be(e2.Id,
            "the persisted cursor must drive the rotation across restarts");

        // Persisted cursor row carries the expected count.
        var cursor = db.ExaminerAssignmentCursors.Single();
        cursor.NextIndex.Should().Be(2);
    }

    // ─────────────────────────── Harness ───────────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-rr-assign-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static async Task<UserProfile> SeedUserAsync(
        CnasDbContext db, string displayName, bool isExaminer)
    {
        var user = new UserProfile
        {
            DisplayName = displayName,
            PreferredLanguage = "ro",
            IsActive = true,
            State = UserAccountState.Active,
            CreatedAtUtc = ClockNow.AddDays(-30),
            Roles = isExaminer ? [ExaminerRole] : [],
            Groups = [],
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// <see cref="CnasDbContext"/> subclass that throws
    /// <see cref="DbUpdateConcurrencyException"/> from the next N
    /// <c>SaveChangesAsync</c> calls AFTER <see cref="ArmThrows"/> is invoked
    /// — used to simulate xmin token contention on the singleton cursor row
    /// without spinning up a real Postgres instance. Seed operations
    /// (SeedUserAsync) execute before the test arms the counter so they
    /// always pass through cleanly.
    /// </summary>
    private sealed class TransientConcurrencyDbContext : CnasDbContext
    {
        private int _remainingThrows;
        private readonly int _initialArm;
        private bool _armed;

        public TransientConcurrencyDbContext(
            DbContextOptions<CnasDbContext> options,
            int throwOnNextSaves)
            : base(options)
        {
            _initialArm = throwOnNextSaves;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // First-time auto-arm: SeedUserAsync calls SaveChanges before the
            // service does, and we want all seed calls to bypass the throw.
            // The auto-arm fires when the change-tracker first contains an
            // ExaminerAssignmentCursor entry — that's the signal that the SUT
            // (not the seed) is driving the save.
            if (!_armed && ChangeTracker.Entries<ExaminerAssignmentCursor>().Any())
            {
                _remainingThrows = _initialArm;
                _armed = true;
            }
            if (_remainingThrows > 0)
            {
                _remainingThrows -= 1;
                throw new DbUpdateConcurrencyException(
                    "Simulated cursor xmin contention (test).");
            }
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
