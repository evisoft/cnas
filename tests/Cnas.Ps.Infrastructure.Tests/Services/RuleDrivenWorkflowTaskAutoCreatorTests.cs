using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — unit tests for
/// <see cref="RuleDrivenWorkflowTaskAutoCreator"/>. The rule-driven auto-creator
/// is the BPM-engine-independent path that materialises
/// <see cref="WorkflowTask"/> rows on application status transitions. The tests
/// pin:
/// <list type="bullet">
///   <item>no rule matches → empty list, nothing staged on the change tracker;</item>
///   <item>single rule matches → exactly one task with the expected kind / role /
///         due date;</item>
///   <item>multiple rules match the same transition → one task per rule (the
///         CF 05.01 "create a notification AND a review task" pattern);</item>
///   <item>inactive rules are skipped (soft-delete);</item>
///   <item>the task's <see cref="WorkflowTask.DueAtUtc"/> is computed from
///         <see cref="WorkflowAutoCreationRule.DueWithinDays"/> against the
///         injected clock;</item>
///   <item>the task's <see cref="WorkflowTask.GroupCode"/> reflects the rule's
///         <see cref="WorkflowAutoCreationRule.AssigneeRole"/>.</item>
/// </list>
/// </summary>
public sealed class RuleDrivenWorkflowTaskAutoCreatorTests
{
    /// <summary>Deterministic UTC clock — pinned for reproducible DueAtUtc math.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-auto-create-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static WorkflowAutoCreationRule BuildRule(
        ApplicationStatus from,
        ApplicationStatus to,
        string taskKind,
        string assigneeRole,
        int dueWithinDays,
        bool active = true) => new()
        {
            FromStatus = from,
            ToStatus = to,
            TaskKind = taskKind,
            AssigneeRole = assigneeRole,
            DueWithinDays = dueWithinDays,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = active,
        };

    /// <summary>
    /// Test helper — seeds a Dossier + ServiceApplication pair so the
    /// auto-creator's "does the dossier exist?" gate is satisfied. The
    /// gate refuses to stage tasks when the application has no associated
    /// dossier (would otherwise emit an FK violation on SaveChanges).
    /// </summary>
    /// <returns>The seeded application id.</returns>
    private static async Task<long> SeedApplicationWithDossierAsync(
        CnasDbContext db,
        long applicationId)
    {
        var dossier = new Dossier
        {
            Id = applicationId, // simple 1:1 pairing keeps the test arithmetic obvious
            ApplicationId = applicationId,
            DossierNumber = $"D-{applicationId}",
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        var application = new ServiceApplication
        {
            Id = applicationId,
            SolicitantId = 1L,
            ServicePassportId = 1L,
            DossierId = dossier.Id,
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        db.Dossiers.Add(dossier);
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return applicationId;
    }

    /// <summary>
    /// When no rule matches the transition, the creator returns an empty list and
    /// does NOT add anything to the change tracker.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_NoRuleMatches_ReturnsEmptyList()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 42L);
        // Add a rule that does NOT match the transition under test.
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-examiner", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 42L,
            from: ApplicationStatus.Submitted,
            to: ApplicationStatus.UnderExamination);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
        db.WorkflowTasks.Local.Should().BeEmpty(
            "no rule matched — nothing must be staged on the change tracker");
    }

    /// <summary>
    /// When exactly one rule matches the transition, one
    /// <see cref="WorkflowTask"/> is staged with the rule's TaskKind embedded in
    /// the title and the assignee group code stamped.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_SingleRuleMatches_CreatesOneTask()
    {
        const long appId = 7L;
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, appId);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-registrar", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: appId,
            from: ApplicationStatus.Draft,
            to: ApplicationStatus.Submitted);

        result.IsSuccess.Should().BeTrue();
        var task = result.Value!.Single();
        task.Title.Should().Contain("INITIAL_REVIEW");
        task.GroupCode.Should().Be("cnas-registrar");
        task.Status.Should().Be(WorkflowTaskStatus.Pending);
        task.AssignedUserId.Should().BeNull(
            "auto-created tasks land in the group inbox until claimed");
        task.IsActive.Should().BeTrue();
        db.WorkflowTasks.Local.Single().Should().BeSameAs(task,
            "the new task MUST be staged on the change tracker so the caller's SaveChanges flushes it together with the transition");
    }

    /// <summary>
    /// When two rules describe the SAME transition with different task kinds, the
    /// auto-creator stages BOTH tasks (CF 05.01's "multiple parallel actors"
    /// fan-out pattern: e.g. on submit create an initial-review AND an
    /// applicant-confirmation task).
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_MultipleRulesMatch_CreatesOneTaskPerRule()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 1L);
        db.WorkflowAutoCreationRules.AddRange(
            BuildRule(ApplicationStatus.Submitted, ApplicationStatus.UnderExamination,
                "EXAMINATION", "cnas-examiner", 7),
            BuildRule(ApplicationStatus.Submitted, ApplicationStatus.UnderExamination,
                "SUPERVISOR_NOTIFY", "cnas-decider", 14));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 1L,
            from: ApplicationStatus.Submitted,
            to: ApplicationStatus.UnderExamination);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value!.Select(t => t.GroupCode).Should().BeEquivalentTo(["cnas-examiner", "cnas-decider"]);
    }

    /// <summary>
    /// An inactive (soft-deleted) rule MUST NOT fire — even if the transition
    /// matches. This pins the IsActive contract so an operator can disable a
    /// rule without dropping it from the table.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_InactiveRule_IsSkipped()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 1L);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Submitted, ApplicationStatus.UnderExamination,
            "EXAMINATION", "cnas-examiner", 7,
            active: false));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 1L,
            from: ApplicationStatus.Submitted,
            to: ApplicationStatus.UnderExamination);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    /// <summary>
    /// The created task's <see cref="WorkflowTask.DueAtUtc"/> is computed from the
    /// rule's <see cref="WorkflowAutoCreationRule.DueWithinDays"/> applied against
    /// the injected clock. The test pins exact equality so a future writer cannot
    /// silently drift the day-shift semantics.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_DueDate_IsClockPlusDueWithinDays()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 1L);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.UnderExamination, ApplicationStatus.PendingApproval,
            "DECIDER_APPROVAL", "cnas-decider", 30));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 1L,
            from: ApplicationStatus.UnderExamination,
            to: ApplicationStatus.PendingApproval);

        result.IsSuccess.Should().BeTrue();
        var task = result.Value!.Single();
        task.DueAtUtc.Should().Be(ClockNow.AddDays(30),
            "the SLA stamp is clock-driven and computed from DueWithinDays");
    }

    /// <summary>
    /// The created task's <see cref="WorkflowTask.GroupCode"/> reflects the
    /// rule's <see cref="WorkflowAutoCreationRule.AssigneeRole"/>. This pins the
    /// role→group mapping is a passthrough today; a future iter can refine the
    /// mapping (e.g. resolve role → group by lookup) without changing the
    /// test's contract beyond the assertion.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_AssigneeRole_IsCopiedToGroupCode()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 1L);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Approved, ApplicationStatus.Closed,
            "PAYMENT_DISPATCH", "cnas-mpay-operator", 3));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 1L,
            from: ApplicationStatus.Approved,
            to: ApplicationStatus.Closed);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().GroupCode.Should().Be("cnas-mpay-operator");
    }

    /// <summary>
    /// The auto-creator MUST NOT call <c>SaveChanges</c> — atomicity is the
    /// caller's responsibility. After the call, the change tracker carries the
    /// new task but the database still has zero rows.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_DoesNotPersist_LeavesSaveChangesToCaller()
    {
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, applicationId: 1L);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-registrar", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        await creator.OnApplicationTransitionAsync(
            applicationId: 1L,
            from: ApplicationStatus.Draft,
            to: ApplicationStatus.Submitted);

        // No SaveChanges → no persisted rows yet (we count over a fresh query, not Local).
        var persisted = await db.WorkflowTasks.LongCountAsync();
        persisted.Should().Be(0L,
            "the auto-creator MUST NOT persist — the caller's transaction owns the SaveChanges boundary");
        db.WorkflowTasks.Local.Should().HaveCount(1,
            "the task is staged in the change tracker so the caller's flush emits it together with the transition");
    }

    /// <summary>
    /// When the application has NO dossier yet (DossierId is null — common for
    /// the Draft → Submitted transition that fires BEFORE the dossier is
    /// materialised), the auto-creator MUST defer: return success with an empty
    /// list and stage nothing. The previous behaviour (DossierId ?? 0L fallback)
    /// emitted an FK violation on SaveChanges that the caller silently swallowed
    /// — leaving the auto-task path dead in production.
    /// </summary>
    /// <remarks>
    /// Companion responsibility: the calling state-machine writer SHOULD
    /// re-invoke the auto-creator after the dossier is materialised so the
    /// deferred tasks actually land. That wiring is owned by another batch;
    /// this test pins the deferred-here behaviour so the early-return cannot
    /// silently regress.
    /// </remarks>
    [Fact]
    public async Task OnApplicationTransitionAsync_NoDossierYet_DefersAndStagesNothing()
    {
        const long appId = 99L;
        await using var db = CreateContext();
        // Seed the application but NOT a dossier — DossierId stays null.
        db.Applications.Add(new ServiceApplication
        {
            Id = appId,
            SolicitantId = 1L,
            ServicePassportId = 1L,
            DossierId = null,
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        });
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-registrar", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: appId,
            from: ApplicationStatus.Draft,
            to: ApplicationStatus.Submitted);

        result.IsSuccess.Should().BeTrue(
            "deferred-because-no-dossier is a normal flow, NOT a failure");
        result.Value!.Should().BeEmpty(
            "no dossier yet → stage nothing; FK to Dossiers would otherwise violate on SaveChanges");
        db.WorkflowTasks.Local.Should().BeEmpty(
            "deferred path leaves the change tracker untouched");
    }

    /// <summary>
    /// When the application is unknown to the database (no row at all — the
    /// caller passes a stale or fake id), the auto-creator MUST also defer
    /// rather than emit an FK-violating row. Pins the same defensive contract
    /// as the no-dossier case but driven by the application-missing branch.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_UnknownApplication_DefersAndStagesNothing()
    {
        await using var db = CreateContext();
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-registrar", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: 12345L, // no Application row exists for this id
            from: ApplicationStatus.Draft,
            to: ApplicationStatus.Submitted);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
        db.WorkflowTasks.Local.Should().BeEmpty();
    }

    /// <summary>
    /// When the dossier exists, the auto-creator stamps the staged task's
    /// <see cref="WorkflowTask.DossierId"/> to the application's real
    /// <c>DossierId</c> — not a 0L fallback that would FK-violate. Together
    /// with the deferred-test this fully pins the dossier-resolution contract.
    /// </summary>
    [Fact]
    public async Task OnApplicationTransitionAsync_DossierExists_StampsRealDossierId()
    {
        const long appId = 11L;
        await using var db = CreateContext();
        await SeedApplicationWithDossierAsync(db, appId);
        db.WorkflowAutoCreationRules.Add(BuildRule(
            ApplicationStatus.Draft, ApplicationStatus.Submitted,
            "INITIAL_REVIEW", "cnas-registrar", 1));
        await db.SaveChangesAsync();
        var creator = new RuleDrivenWorkflowTaskAutoCreator(db, new StubClock());

        var result = await creator.OnApplicationTransitionAsync(
            applicationId: appId,
            from: ApplicationStatus.Draft,
            to: ApplicationStatus.Submitted);

        result.IsSuccess.Should().BeTrue();
        var task = result.Value!.Single();
        task.DossierId.Should().Be(appId,
            "the seeded dossier id matches the application id (1:1 in this test) and MUST be stamped on the staged task");
        task.DossierId.Should().NotBe(0L,
            "0L was the buggy fallback that emitted an FK violation");
    }
}
