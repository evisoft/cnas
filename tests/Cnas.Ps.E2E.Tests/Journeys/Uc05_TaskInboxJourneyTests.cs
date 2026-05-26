using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC05 — "Execut sarcini". End-to-end journey covering an examiner's workflow task
/// inbox: list assigned work, claim a group-inbox task, complete it, and verify that an
/// unrelated examiner cannot complete a task they don't own (403 — the
/// <see cref="ErrorCodes.WorkflowNotAssignee"/> guard).
/// </summary>
/// <remarks>
/// <para>
/// <b>Active journey.</b> Previously skipped because no controller exposed
/// <see cref="Cnas.Ps.Application.UseCases.ITaskInboxService"/>; the matching
/// <c>TasksController</c> now wires the surface and this journey exercises it
/// end-to-end through the authenticated host fixture.
/// </para>
/// <para>
/// <b>What this journey asserts.</b>
/// <list type="number">
///   <item>An examiner persona lists their inbox via <c>GET /api/tasks</c> and receives a
///         <see cref="PagedResult{T}"/> of <see cref="TaskInboxItem"/> rows with
///         Sqid-encoded ids (never raw <c>long</c>).</item>
///   <item>The persona claims a previously-unassigned (group-inbox) task via
///         <c>POST /api/tasks/{id}/claim</c> and the underlying
///         <see cref="WorkflowTask.AssignedUserId"/> flips to their id.</item>
///   <item>The persona completes the now-owned task via
///         <c>POST /api/tasks/{id}/complete</c> with a result payload; the row
///         transitions to <see cref="WorkflowTaskStatus.Completed"/> with
///         <see cref="WorkflowTask.CompletedAtUtc"/> stamped.</item>
///   <item>An unrelated persona attempting to complete a task they don't own receives
///         403 Forbidden — the <see cref="ErrorCodes.WorkflowNotAssignee"/> guard
///         surfaces correctly through the controller.</item>
/// </list>
/// </para>
/// <para>
/// <b>Claim-race assertion.</b> After the second caller already owns
/// <c>groupTask</c>, an unrelated examiner re-claiming the same row must receive 403 /
/// <see cref="ErrorCodes.WorkflowNotAssignee"/> — the deny-by-default guard introduced
/// when the historical <c>TODO claim-race</c> in <c>TaskInboxService.ClaimAsync</c> was
/// resolved. The journey also covers the wrong-assignee-on-complete path
/// (<see cref="ErrorCodes.WorkflowNotAssignee"/> via <c>POST /complete</c>) so both
/// double-mutation entry points are pinned end-to-end.
/// </para>
/// <para>
/// <b>PII discipline.</b> The seed data contains no real IDNPs and the assertions log
/// only Sqid-encoded identifiers — never the raw database <c>long</c> — per the
/// CLAUDE.md RULE 3 / SEC 042 guidance.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc05_TaskInboxJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc05_TaskInboxJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Drives the full list → claim → complete sequence for a single examiner persona.
    /// Seeds two tasks against a fresh dossier — one already assigned to the persona, one
    /// unassigned (group inbox) — then exercises every endpoint and asserts the DB
    /// transitions match the HTTP contract.
    /// </summary>
    [Fact]
    public async Task TaskInbox_ListClaimCompleteFlow_ThroughHttp()
    {
        // Arrange — seed Solicitant → ServicePassport → Application → Dossier → tasks.
        // The persona's id is a stable test number; the dossier number is randomised so
        // re-runs against the shared fixture do not collide on the unique index.
        const long examinerUserId = 50_500L;
        await using var seedScope = _fixture.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = seedScope.ServiceProvider.GetRequiredService<ISqidService>();
        var nowUtc = DateTime.UtcNow;

        var dossierId = await SeedDossierAsync(db, nowUtc);

        // Task 1 — already assigned to our persona; we'll complete this one.
        var assignedTask = new WorkflowTask
        {
            CreatedAtUtc = nowUtc,
            DossierId = dossierId,
            Title = "UC05 — examinare dosar (assigned)",
            Status = WorkflowTaskStatus.Pending,
            AssignedUserId = examinerUserId,
            GroupCode = "cnas-examiner",
            DueAtUtc = nowUtc.AddDays(3),
            IsActive = true,
        };

        // Task 2 — group inbox (no AssignedUserId yet); we'll claim then complete it.
        var groupTask = new WorkflowTask
        {
            CreatedAtUtc = nowUtc,
            DossierId = dossierId,
            Title = "UC05 — examinare dosar (group)",
            Status = WorkflowTaskStatus.Pending,
            AssignedUserId = null,
            GroupCode = "cnas-examiner",
            DueAtUtc = nowUtc.AddDays(5),
            IsActive = true,
        };

        db.WorkflowTasks.Add(assignedTask);
        db.WorkflowTasks.Add(groupTask);
        await db.SaveChangesAsync();

        var examinerSqid = sqids.Encode(examinerUserId);
        var assignedTaskSqid = sqids.Encode(assignedTask.Id);
        var groupTaskSqid = sqids.Encode(groupTask.Id);

        // Build the examiner's HTTP client — cnas-decider role satisfies the
        // CnasDecider authorization policy on the controller.
        using var client = NewClient(examinerSqid, "cnas-decider");

        // Act 1 — list inbox. The pre-seeded assigned task must appear; group task
        // does NOT yet (no AssignedUserId), per the service's filter.
        using var listResponse = await client.GetAsync("/api/tasks?page=1&pageSize=20");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await listResponse.Content.ReadAsStringAsync());
        var listed = await listResponse.Content.ReadFromJsonAsync<PagedResult<TaskInboxItem>>();
        listed.Should().NotBeNull();
        var listedAssigned = listed!.Items.SingleOrDefault(t => t.Id == assignedTaskSqid);
        listedAssigned.Should().NotBeNull("the pre-assigned task must surface in the inbox");
        listedAssigned!.Title.Should().Be("UC05 — examinare dosar (assigned)");
        listedAssigned.Status.Should().Be(nameof(WorkflowTaskStatus.Pending));
        // No raw IDs over the wire — Sqid strings only (CLAUDE.md RULE 3).
        AssertIsSqid(listedAssigned.Id);
        AssertIsSqid(listedAssigned.DossierId);

        // Act 2 — claim the group task. The service flips AssignedUserId to the caller.
        using var claimResponse = await client.PostAsync(
            $"/api/tasks/{groupTaskSqid}/claim", content: null);
        claimResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await claimResponse.Content.ReadAsStringAsync());

        await using (var assertScope = _fixture.Services.CreateAsyncScope())
        {
            var assertDb = assertScope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var refreshed = await assertDb.WorkflowTasks.AsNoTracking()
                .SingleAsync(t => t.Id == groupTask.Id);
            refreshed.AssignedUserId.Should().Be(examinerUserId,
                "ClaimAsync must flip AssignedUserId to the calling user");
            refreshed.Status.Should().Be(WorkflowTaskStatus.InProgress);
        }

        // Act 3 — complete the now-owned task with a result payload.
        var completePayload = new CompleteTaskRequest("{\"verdict\":\"approved\"}");
        using var completeResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{groupTaskSqid}/complete", completePayload);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await completeResponse.Content.ReadAsStringAsync());

        await using (var assertScope = _fixture.Services.CreateAsyncScope())
        {
            var assertDb = assertScope.ServiceProvider.GetRequiredService<CnasDbContext>();
            var refreshed = await assertDb.WorkflowTasks.AsNoTracking()
                .SingleAsync(t => t.Id == groupTask.Id);
            refreshed.Status.Should().Be(WorkflowTaskStatus.Completed,
                "CompleteAsync must transition the row to Completed");
            refreshed.CompletedAtUtc.Should().NotBeNull("CompletedAtUtc must be stamped");
        }

        // Act 4 — an unrelated examiner tries to complete the OTHER task (still owned
        // by `examinerUserId`). Expect 403 — the WORKFLOW_NOT_ASSIGNEE guard.
        const long unrelatedUserId = 50_501L;
        using var unrelatedClient = NewClient(sqids.Encode(unrelatedUserId), "cnas-decider");
        var stealPayload = new CompleteTaskRequest("{\"verdict\":\"steal\"}");
        using var stealResponse = await unrelatedClient.PostAsJsonAsync(
            $"/api/tasks/{assignedTaskSqid}/complete", stealPayload);
        stealResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "WORKFLOW_NOT_ASSIGNEE must surface as 403 — only the assignee may complete");

        // The DB row for the assigned task must NOT have been mutated by the rejected call.
        await using var finalScope = _fixture.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var stillPending = await finalDb.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.Id == assignedTask.Id);
        stillPending.Status.Should().Be(WorkflowTaskStatus.Pending,
            "the 403 guard must not advance the task state");
        stillPending.CompletedAtUtc.Should().BeNull();

        // Act 5 — double-claim guard. The unrelated examiner attempts to claim the
        // assigned task already owned by `examinerUserId`. Expect 403 — the deny-by-default
        // guard introduced when the historical claim-race TODO was resolved (see the
        // matching infra unit test
        // TaskInboxServiceTests.ClaimAsync_AlreadyAssignedToAnotherUser_ReturnsWorkflowNotAssignee).
        using var doubleClaimResponse = await unrelatedClient.PostAsync(
            $"/api/tasks/{assignedTaskSqid}/claim", content: null);
        doubleClaimResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a task already claimed by another user must not be re-claimable — WORKFLOW_NOT_ASSIGNEE → 403");

        // And the DB row must STILL be owned by the original assignee.
        await using var doubleClaimScope = _fixture.Services.CreateAsyncScope();
        var doubleClaimDb = doubleClaimScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var untouched = await doubleClaimDb.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.Id == assignedTask.Id);
        untouched.AssignedUserId.Should().Be(examinerUserId,
            "the rejected double-claim must not transfer ownership");
    }

    /// <summary>
    /// Seeds the minimum Solicitant → ServicePassport → ServiceApplication → Dossier
    /// graph required to satisfy WorkflowTask's foreign-key contract and returns the
    /// dossier id. Mirrors the helper in <c>TaskInboxServiceTests</c> so the two test
    /// suites stay in lock-step on the schema graph.
    /// </summary>
    /// <param name="db">Live <see cref="CnasDbContext"/> (in-memory provider in tests).</param>
    /// <param name="nowUtc">Stable UTC clock value to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>The newly-created dossier id (internal long).</returns>
    private static async Task<long> SeedDossierAsync(CnasDbContext db, DateTime nowUtc)
    {
        // Synthetic, never-real IDNP — pinned at the documented test placeholder so the
        // assertion log carries no real personal data (CLAUDE.md PII discipline).
        var solicitant = new Solicitant
        {
            CreatedAtUtc = nowUtc,
            NationalId = "2000000000050",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC05 E2E Solicitant",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);

        var passport = new ServicePassport
        {
            CreatedAtUtc = nowUtc,
            Code = $"SP-UC05-{Guid.NewGuid():N}",
            NameRo = "UC05 test passport",
            DescriptionRo = "UC05 test passport",
            FormSchemaJson = "{}",
            WorkflowCode = "WF-UC05-TEST",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = nowUtc,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.UnderExamination,
            FormPayloadJson = "{}",
            SnapshotJson = "{}",
            IsActive = true,
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();

        var dossier = new Dossier
        {
            CreatedAtUtc = nowUtc,
            ApplicationId = app.Id,
            DossierNumber = $"D-2026-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            IsActive = true,
        };
        db.Dossiers.Add(dossier);
        await db.SaveChangesAsync();
        return dossier.Id;
    }

    /// <summary>
    /// Builds a fresh <see cref="HttpClient"/> bound to the fixture's base address and
    /// authenticated as the supplied persona via the <see cref="TestAuthHandler"/>.
    /// </summary>
    /// <param name="subSqid">Persona's Sqid-encoded subject id.</param>
    /// <param name="role">CNAS role code (e.g. <c>cnas-decider</c>).</param>
    /// <returns>HttpClient that callers must dispose.</returns>
    private HttpClient NewClient(string subSqid, string role)
    {
        var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: subSqid, Roles: [role])));
        return client;
    }

    /// <summary>
    /// Defensive assertion: every <c>id</c> / <c>dossierId</c> field on the wire MUST be
    /// a non-numeric Sqid of at least 5 characters. Numeric-only strings or empty
    /// strings would indicate a regression that exposed raw primary keys (CLAUDE.md
    /// RULE 3 violation).
    /// </summary>
    /// <param name="sqid">The candidate Sqid string from a JSON response.</param>
    private static void AssertIsSqid(string sqid)
    {
        sqid.Should().NotBeNullOrWhiteSpace();
        sqid.Length.Should().BeGreaterOrEqualTo(5, "Sqid:MinLength is configured to >= 5");
        long.TryParse(sqid, out _).Should().BeFalse(
            "raw numeric primary keys must never appear on the wire — see CLAUDE.md RULE 3");
    }
}
