using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="DocumentExaminationService"/> (UC08 — examiner workflow).
/// Uses EF Core InMemory + NSubstitute. The <see cref="IDocumentGenerationService"/>
/// collaborator is substituted to keep the tests focused on the examiner-workflow logic
/// (the generator's own behaviour is covered by <c>DocumentGenerationServiceTests</c>).
/// </summary>
public class DocumentExaminationServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string DocSqid = "DOC-SQID";
    private const string DossierSqid = "DOSS-SQID";

    // ─────────────────────── RecordVerdictAsync ───────────────────────

    [Fact]
    public async Task RecordVerdictAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.RecordVerdictAsync("bad", ExaminationVerdict.Accepted, note: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task RecordVerdictAsync_DocumentNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.RecordVerdictAsync("missing", ExaminationVerdict.Accepted, note: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task RecordVerdictAsync_HappyPath_SetsFieldsAndAudits()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.RecordVerdictAsync(DocSqid, ExaminationVerdict.Rejected, note: "missing seal");

        result.IsSuccess.Should().BeTrue();
        var doc = await harness.Db.Documents.SingleAsync(d => d.Id == seeded.DocumentId);
        doc.Verdict.Should().Be((int)ExaminationVerdict.Rejected);
        doc.VerdictNote.Should().Be("missing seal");
        doc.VerdictAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "DOCUMENT.Rejected",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Document),
            seeded.DocumentId,
            Arg.Is<string>(s => s.Contains("missing seal", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── GenerateDraftsAsync ───────────────────────

    [Fact]
    public async Task GenerateDraftsAsync_HappyPath_ReturnsTwoSqidIds()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        harness.DocGen.GenerateCalculationSheetAsync(DossierSqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<string>.Success("SHEET-SQID")));
        harness.DocGen.GenerateDecisionAsync(DossierSqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<string>.Success("DEC-SQID")));

        var result = await harness.Service.GenerateDraftsAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();
        result.Value.CalculationSheetId.Should().Be("SHEET-SQID");
        result.Value.DecisionId.Should().Be("DEC-SQID");

        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.DRAFTS_GENERATED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Dossier),
            Arg.Any<long?>(),
            Arg.Is<string>(s => s.Contains("SHEET-SQID", StringComparison.Ordinal)
                                 && s.Contains("DEC-SQID", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── SubmitForApprovalAsync ───────────────────────

    [Fact]
    public async Task SubmitForApprovalAsync_CallerNotExaminer_ReturnsForbidden()
    {
        var harness = Harness.Create();
        // Assign the dossier to a different examiner (userId=42), but the caller is userId=1.
        await harness.SeedAsync(assignedExaminerId: 42L);

        var result = await harness.Service.SubmitForApprovalAsync(DossierSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task SubmitForApprovalAsync_HappyPath_FlipsAppToPendingApproval_CompletesExaminerTask_OpensDeciderTask()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L); // matches caller.UserId.

        var result = await harness.Service.SubmitForApprovalAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.PendingApproval);

        var tasks = await harness.Db.WorkflowTasks
            .Where(t => t.DossierId == seeded.DossierId)
            .ToListAsync();
        tasks.Should().HaveCount(2);
        tasks.Should().Contain(t =>
            t.GroupCode == "cnas-examiner" && t.Status == WorkflowTaskStatus.Completed);
        tasks.Should().Contain(t =>
            t.GroupCode == "cnas-decider"
            && t.Status == WorkflowTaskStatus.Pending
            && t.Title == "Aprobare decizie");

        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.SUBMITTED_FOR_APPROVAL",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await harness.Notify.Received(1).EnqueueAsync(
            seeded.SolicitantId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitForApprovalAsync_FromInvalidState_ReturnsConflict()
    {
        // iter-149 — SubmitForApproval only flows from UnderExamination or
        // Submitted. Mutating from a terminal/intermediate status (e.g.
        // Approved, RejectedIncomplete, Returned, Closed) would silently
        // overwrite the verdict — surface a Conflict instead.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L);
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status = ApplicationStatus.Approved; // not a legal predecessor of PendingApproval.
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.SubmitForApprovalAsync(DossierSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);

        // The status must not have been overwritten by the failed mutation.
        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        reloaded.Status.Should().Be(ApplicationStatus.Approved);
    }

    // ─────────────────────── RefuseAsync ───────────────────────

    [Fact]
    public async Task RefuseAsync_HappyPath_SetsRejectedAndCancelsTasks()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L);

        var result = await harness.Service.RefuseAsync(DossierSqid, reason: "Documents incomplete.");

        result.IsSuccess.Should().BeTrue();

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Rejected);
        app.ClosedAtUtc.Should().Be(ClockNow);

        var dossier = await harness.Db.Dossiers.SingleAsync(d => d.Id == seeded.DossierId);
        dossier.ClosedAtUtc.Should().Be(ClockNow);

        var tasks = await harness.Db.WorkflowTasks.Where(t => t.DossierId == seeded.DossierId).ToListAsync();
        tasks.Should().NotBeEmpty();
        tasks.Should().OnlyContain(t => t.Status == WorkflowTaskStatus.Cancelled);

        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.REFUSED_BY_EXAMINER",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s => s.Contains("Documents incomplete.", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await harness.Notify.Received(1).EnqueueAsync(
            seeded.SolicitantId,
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("Documents incomplete.", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefuseAsync_CallerNotExaminer_ReturnsForbidden()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 42L);

        var result = await harness.Service.RefuseAsync(DossierSqid, reason: "test");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-doce-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long AppId, long SolicitantId, long DocumentId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DocumentExaminationService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required INotificationService Notify { get; init; }
        public required IDocumentGenerationService DocGen { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var docgen = Substitute.For<IDocumentGenerationService>();
            docgen.GenerateCalculationSheetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<string>.Success("SHEET-SQID")));
            docgen.GenerateDecisionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<string>.Success("DEC-SQID")));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(["cnas-examiner"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            // MCabinet publisher substitute — returns success so the best-effort outbound
            // projection wired into GenerateDrafts/SubmitForApproval/Refuse is a no-op for
            // these examiner-workflow tests.
            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var service = new DocumentExaminationService(
                db, sqids, clock, caller, docgen, audit, notify, mcabinet,
                NullLogger<DocumentExaminationService>.Instance);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Notify = notify,
                DocGen = docgen,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        public async Task<SeedResult> SeedAsync(long? assignedExaminerId = null)
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
                NameRo = "Test",
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
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-ABCD1234",
                AssignedExaminerId = assignedExaminerId,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            // Seed an existing examiner workflow task (the one auto-created on submission).
            var task = new WorkflowTask
            {
                DossierId = dossier.Id,
                Title = "Examinare cerere",
                GroupCode = "cnas-examiner",
                Status = WorkflowTaskStatus.Pending,
                DueAtUtc = ClockNow.AddDays(30),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);

            // Seed an attached document so RecordVerdictAsync tests have something to update.
            var doc = new Document
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossier.Id,
                Kind = DocumentKind.Attachment,
                Title = "id-card.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = "test-key",
                StorageBucket = "cnas-citizen-uploads",
                ContentSha256Hex = new string('a', 64),
                IsActive = true,
            };
            Db.Documents.Add(doc);
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));
            Sqids.TryDecode(DocSqid).Returns(Result<long>.Success(doc.Id));

            return new SeedResult(dossier.Id, app.Id, solicitant.Id, doc.Id);
        }
    }
}
