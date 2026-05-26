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
/// R0590 / TOR CF 10.01 — integration tests for <see cref="ApprovalWorkspaceService"/>.
/// Drives the SUT against an EF Core InMemory store; substitutes the Sqid encoder
/// with a trivial fake so encoded ids remain assertion-friendly. Each test
/// exercises a single observable behaviour: empty queue, a single pending row,
/// multi-row counts, overdue accounting, and the cross-check that the summary
/// matches the list page.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were written BEFORE the service body;
/// they form the red half of the TDD cycle that drove the production code.
/// </remarks>
public sealed class ApprovalWorkspaceServiceTests
{
    /// <summary>Deterministic UTC clock so today/overdue boundaries are reproducible.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSummaryAsync_NoPendingDossiers_ReturnsZeroes()
    {
        // Arrange — no Applications or Dossiers seeded, so the pending-approval queue is empty.
        var harness = Harness.Create();

        // Act
        var result = await harness.Service.GetSummaryAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        var summary = result.Value!;
        summary.PendingCount.Should().Be(0);
        summary.OverdueCount.Should().Be(0);
        summary.TodayCount.Should().Be(0);
    }

    [Fact]
    public async Task ListPendingAsync_NoPendingDossiers_ReturnsEmptyPage()
    {
        var harness = Harness.Create();

        var result = await harness.Service.ListPendingAsync(page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        var page = result.Value!;
        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task ListPendingAsync_SinglePendingDossier_ReturnsOneRow()
    {
        // Arrange — one application + dossier in PendingApproval state.
        var harness = Harness.Create();
        var seed = await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddHours(-1));

        // Act
        var result = await harness.Service.ListPendingAsync(page: 1, pageSize: 20);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var page = result.Value!;
        page.Items.Should().HaveCount(1);
        page.TotalCount.Should().Be(1);

        var row = page.Items[0];
        row.Id.Should().Be($"SQID-{seed.DossierId}");
        row.DossierCode.Should().Be(seed.DossierNumber);
        row.DecisionTitle.Should().Be("Pensie pentru limită de vârstă");
        row.ExaminerName.Should().Be("Maria Examinator");
        row.ExaminerSqid.Should().Be($"SQID-{seed.ExaminerUserId}");
        row.SlaDeadlineUtc.Should().Be(ClockNow.AddDays(3));
        row.EmittedAtUtc.Should().Be(ClockNow.AddHours(-1));
    }

    [Fact]
    public async Task ListPendingAsync_MultiplePending_ReturnsAllOrderedBySlaDeadlineAsc()
    {
        // Arrange — three pending dossiers with staggered SLA deadlines.
        var harness = Harness.Create();
        var far = await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(10),
            emittedAtUtc: ClockNow.AddHours(-2),
            dossierNumber: "D-2026-FAR");
        var near = await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(1),
            emittedAtUtc: ClockNow.AddHours(-1),
            dossierNumber: "D-2026-NEAR");
        var mid = await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(5),
            emittedAtUtc: ClockNow.AddHours(-3),
            dossierNumber: "D-2026-MID");

        // Act
        var result = await harness.Service.ListPendingAsync(page: 1, pageSize: 20);

        // Assert — ordered by deadline ascending: NEAR (D+1) → MID (D+5) → FAR (D+10).
        result.IsSuccess.Should().BeTrue();
        var items = result.Value!.Items;
        items.Should().HaveCount(3);
        items[0].DossierCode.Should().Be("D-2026-NEAR");
        items[1].DossierCode.Should().Be("D-2026-MID");
        items[2].DossierCode.Should().Be("D-2026-FAR");

        // Sanity-check: ids round-trip through the Sqid stub.
        items[0].Id.Should().Be($"SQID-{near.DossierId}");
        items[1].Id.Should().Be($"SQID-{mid.DossierId}");
        items[2].Id.Should().Be($"SQID-{far.DossierId}");
    }

    [Fact]
    public async Task GetSummaryAsync_OverdueDossiers_CountedInOverdueBucket()
    {
        // Arrange — two dossiers in the queue: one with a past-due SLA, one with a future SLA.
        var harness = Harness.Create();
        await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(-1), // past — overdue
            emittedAtUtc: ClockNow.AddDays(-5),
            dossierNumber: "D-2026-LATE");
        await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(2), // future — not overdue
            emittedAtUtc: ClockNow.AddDays(-5),
            dossierNumber: "D-2026-OK");

        // Act
        var result = await harness.Service.GetSummaryAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        var summary = result.Value!;
        summary.PendingCount.Should().Be(2);
        summary.OverdueCount.Should().Be(1, "only D-2026-LATE has an SLA deadline in the past");
        // Neither dossier landed on the queue TODAY (both stamped 5 days ago) so TodayCount = 0.
        summary.TodayCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_TodaysDossiersCounted_AndPendingMatchesListTotal()
    {
        // Arrange — one row that landed today, one that landed yesterday. Both pending.
        var harness = Harness.Create();
        await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddMinutes(-10), // landed today (within the UTC day)
            dossierNumber: "D-2026-TODAY");
        await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddDays(-1).AddHours(2), // landed yesterday
            dossierNumber: "D-2026-YESTERDAY");

        // Act
        var summaryResult = await harness.Service.GetSummaryAsync();
        var listResult = await harness.Service.ListPendingAsync(page: 1, pageSize: 20);

        // Assert
        summaryResult.IsSuccess.Should().BeTrue();
        listResult.IsSuccess.Should().BeTrue();

        summaryResult.Value!.PendingCount.Should().Be(2);
        summaryResult.Value!.TodayCount.Should().Be(1, "only the row stamped after midnight UTC counts");
        summaryResult.Value!.OverdueCount.Should().Be(0);

        // Cross-check: the summary's PendingCount MUST match the list's TotalCount —
        // the two surfaces project the same queue.
        listResult.Value!.TotalCount.Should().Be(summaryResult.Value!.PendingCount);
    }

    [Fact]
    public async Task ListPendingAsync_TerminalApplications_AreNotIncluded()
    {
        // Arrange — an Approved + a Rejected dossier should NOT appear in the queue;
        // only PendingApproval dossiers do.
        var harness = Harness.Create();
        await harness.SeedDossierAsync(
            status: ApplicationStatus.Approved,
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddHours(-1),
            dossierNumber: "D-2026-APPROVED");
        await harness.SeedDossierAsync(
            status: ApplicationStatus.Rejected,
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddHours(-1),
            dossierNumber: "D-2026-REJECTED");
        // Just one PendingApproval row.
        var pending = await harness.SeedPendingAsync(
            slaDeadlineUtc: ClockNow.AddDays(3),
            emittedAtUtc: ClockNow.AddHours(-1),
            dossierNumber: "D-2026-PENDING");

        // Act
        var listResult = await harness.Service.ListPendingAsync(page: 1, pageSize: 20);
        var summaryResult = await harness.Service.GetSummaryAsync();

        // Assert
        listResult.IsSuccess.Should().BeTrue();
        listResult.Value!.Items.Should().HaveCount(1);
        listResult.Value!.Items[0].DossierCode.Should().Be(pending.DossierNumber);
        summaryResult.Value!.PendingCount.Should().Be(1);
    }

    // ───────────── Harness ─────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long ApplicationId, long ExaminerUserId, string DossierNumber);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApprovalWorkspaceService Service { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-approval-ws-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = NSubstitute.Substitute.For<ISqidService>();
            sqids.Encode(NSubstitute.Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var service = new ApprovalWorkspaceService(db, sqids, clock);
            return new Harness { Db = db, Service = service };
        }

        public Task<SeedResult> SeedPendingAsync(
            DateTime slaDeadlineUtc,
            DateTime emittedAtUtc,
            string? dossierNumber = null)
            => SeedDossierAsync(
                ApplicationStatus.PendingApproval,
                slaDeadlineUtc,
                emittedAtUtc,
                dossierNumber);

        public async Task<SeedResult> SeedDossierAsync(
            ApplicationStatus status,
            DateTime slaDeadlineUtc,
            DateTime emittedAtUtc,
            string? dossierNumber = null)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                NationalId = $"2{Guid.NewGuid().ToString("N")[..12]}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                Code = $"SP-{Guid.NewGuid().ToString("N")[..8]}",
                NameRo = "Pensie pentru limită de vârstă",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-PENS",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);

            var examiner = new UserProfile
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                LocalLogin = $"ex-{Guid.NewGuid().ToString("N")[..6]}",
                Email = $"ex-{Guid.NewGuid().ToString("N")[..6]}@test",
                DisplayName = "Maria Examinator",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.UserProfiles.Add(examiner);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = emittedAtUtc,
                UpdatedAtUtc = emittedAtUtc,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = status,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = emittedAtUtc.AddHours(-1),
                ReferenceNumber = $"PS-{Guid.NewGuid().ToString("N")[..6]}",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossierNum = dossierNumber ?? $"D-2026-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
            var dossier = new Dossier
            {
                CreatedAtUtc = emittedAtUtc,
                UpdatedAtUtc = emittedAtUtc,
                ApplicationId = app.Id,
                DossierNumber = dossierNum,
                AssignedExaminerId = examiner.Id,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            app.DossierId = dossier.Id;

            // Open decider task carrying the SLA deadline — this is the row the
            // approval workspace projects to populate the SlaDeadlineUtc + emitted-at
            // semantics (mirrors the SubmitForApproval path in DocumentExaminationService).
            var deciderTask = new WorkflowTask
            {
                DossierId = dossier.Id,
                Title = "Aprobare decizie",
                GroupCode = "cnas-decider",
                Status = WorkflowTaskStatus.Pending,
                DueAtUtc = slaDeadlineUtc,
                UnclaimedSinceUtc = emittedAtUtc,
                CreatedAtUtc = emittedAtUtc,
                UpdatedAtUtc = emittedAtUtc,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(deciderTask);
            await Db.SaveChangesAsync();

            return new SeedResult(dossier.Id, app.Id, examiner.Id, dossierNum);
        }
    }
}
