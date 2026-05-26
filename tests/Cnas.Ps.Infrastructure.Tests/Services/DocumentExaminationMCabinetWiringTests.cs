using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests covering MCabinet publish wiring on the
/// <see cref="DocumentExaminationService"/> examiner-workflow transitions:
/// <c>GenerateDrafts → DraftReady</c>, <c>SubmitForApproval → InExamination</c>,
/// <c>Refuse → Rejected</c>. The publisher is substituted; failures must be swallowed
/// (best-effort projection, dossier state is the source of truth).
/// </summary>
public class DocumentExaminationMCabinetWiringTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string DossierSqid = "DOSS-SQID";
    private const string SolicitantIdnp = "2000000000007";
    private const string PassportCode = "SP-TEST";
    private const string PassportNameRo = "Test passport";

    // ─────────────────────── GenerateDraftsAsync ───────────────────────

    [Fact]
    public async Task GenerateDraftsAsync_Success_PublishesDraftReadyCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        var result = await harness.Service.GenerateDraftsAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                !string.IsNullOrWhiteSpace(c.ExternalId)
                && c.CitizenIdnp == SolicitantIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.DraftReady
                && c.TitleRo == PassportNameRo
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── SubmitForApprovalAsync ───────────────────────

    [Fact]
    public async Task SubmitForApprovalAsync_Success_PublishesInExaminationCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L); // matches caller.UserId

        var result = await harness.Service.SubmitForApprovalAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                c.CitizenIdnp == SolicitantIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.InExamination
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── RefuseAsync ───────────────────────

    [Fact]
    public async Task RefuseAsync_Success_PublishesRejectedCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L);

        var result = await harness.Service.RefuseAsync(DossierSqid, reason: "Documents incomplete.");

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                c.CitizenIdnp == SolicitantIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Rejected
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefuseAsync_PublisherFails_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L);
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet down."));

        var result = await harness.Service.RefuseAsync(DossierSqid, reason: "Documents incomplete.");

        // Best-effort projection — refusal must persist regardless of the citizen-card publish.
        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Rejected);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-doce-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long AppId, long SolicitantId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DocumentExaminationService Service { get; init; }
        public required IMCabinetPublisher MCabinet { get; init; }
        public required ILogger<DocumentExaminationService> Logger { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }

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

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<DocumentExaminationService>>();

            var service = new DocumentExaminationService(
                db, sqids, clock, caller, docgen, audit, notify, mcabinet, logger);
            return new Harness
            {
                Db = db,
                Service = service,
                MCabinet = mcabinet,
                Logger = logger,
                Caller = caller,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync(long? assignedExaminerId = null)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = SolicitantIdnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = PassportCode,
                NameRo = PassportNameRo,
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
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));

            return new SeedResult(dossier.Id, app.Id, solicitant.Id);
        }
    }
}
