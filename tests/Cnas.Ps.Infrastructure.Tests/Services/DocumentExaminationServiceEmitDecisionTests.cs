using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0573 / TOR CF 08.05 — integration tests for
/// <see cref="DocumentExaminationService.EmitNewDecisionAsync"/>. Uses EF Core
/// InMemory + NSubstitute. The <see cref="IDocumentGenerationService"/>
/// collaborator is substituted so the tests stay focused on the emit-decision
/// branches (template validation, editable-state guard, audit fan-out, trigger
/// dispatch).
/// </summary>
public sealed class DocumentExaminationServiceEmitDecisionTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
    private const string DossierSqid = "DOSS-SQID";
    private const string KnownTemplateCode = "decizia-pensie";
    private const string NewDocumentSqid = "NEW-DOC-SQID";

    [Fact]
    public async Task EmitNewDecisionAsync_HappyPath_PersistsDocumentAndAuditsAndDispatchesTrigger()
    {
        // iter-149 — happy path no longer carries an OverrideAmount because the
        // generation engine does not yet honour the override hook; the dedicated
        // "OverrideAmount.HasValue → NotImplemented" test below pins that
        // refusal, and the happy path now exercises the engine-driven flow.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L);
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: KnownTemplateCode,
            Notes: "Decizie nouă conform CF 08.05.",
            OverrideAmount: null);

        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.DocumentId.Should().Be(NewDocumentSqid);
        result.Value.DecisionTemplateCode.Should().Be(KnownTemplateCode);

        await harness.DocGen.Received(1).GenerateDecisionAsync(
            DossierSqid,
            Arg.Any<CancellationToken>());

        await harness.Audit.Received(1).RecordAsync(
            "EXAMINATION.NEW_DECISION_EMITTED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s =>
                s.Contains(KnownTemplateCode, StringComparison.Ordinal)
                && s.Contains("Decizie", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await harness.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Is<NotificationTriggerPayload>(p =>
                p.RecipientUserId == seeded.SolicitantId
                && p.RelatedEntityType == NotificationRelatedEntityTypes.Application),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitNewDecisionAsync_WithOverrideAmount_ReturnsNotImplemented()
    {
        // iter-149 — until the renderer learns to honour the operator-supplied
        // override, accepting OverrideAmount.HasValue would silently emit the
        // engine outcome while only the audit trail carried the override. We
        // refuse with NotImplemented so the UI can hide the override field and
        // the operator does not receive a deceptive success.
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L);
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: KnownTemplateCode,
            Notes: "Cu override pentru CF 08.05.",
            OverrideAmount: 2500.00m);

        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotImplemented);

        // No generation, no audit, no trigger dispatch — the refusal short-circuits.
        await harness.DocGen.DidNotReceiveWithAnyArgs().GenerateDecisionAsync(default!, default);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        await harness.Triggers.DidNotReceiveWithAnyArgs().DispatchAsync(
            default, default!, default);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_TerminalStatus_ReturnsExaminationNotEditable()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(assignedExaminerId: 1L);
        // Flip the application to Approved — a terminal status — and persist.
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status = ApplicationStatus.Approved;
        await harness.Db.SaveChangesAsync();

        var dto = new EmitNewDecisionInputDto(KnownTemplateCode, null, null);

        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExaminationNotEditable);

        await harness.DocGen.DidNotReceiveWithAnyArgs().GenerateDecisionAsync(default!, default);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_UnknownTemplate_ReturnsDocumentTemplateNotFound()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L);
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "this-template-does-not-exist",
            Notes: null,
            OverrideAmount: null);

        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.DocumentTemplateNotFound);

        await harness.DocGen.DidNotReceiveWithAnyArgs().GenerateDecisionAsync(default!, default);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_CallerNotAssignedExaminer_ReturnsForbidden()
    {
        var harness = Harness.Create();
        // Assigned examiner = 42; caller is userId=1 (set on harness).
        await harness.SeedAsync(assignedExaminerId: 42L);
        var dto = new EmitNewDecisionInputDto(KnownTemplateCode, null, null);

        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);

        await harness.DocGen.DidNotReceiveWithAnyArgs().GenerateDecisionAsync(default!, default);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_DossierNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));
        var dto = new EmitNewDecisionInputDto(KnownTemplateCode, null, null);

        var result = await harness.Service.EmitNewDecisionAsync("missing", dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));
        var dto = new EmitNewDecisionInputDto(KnownTemplateCode, null, null);

        var result = await harness.Service.EmitNewDecisionAsync("bad", dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task EmitNewDecisionAsync_TriggerThrows_DoesNotRollbackStateMachine()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L);
        // Make the trigger dispatcher throw — best-effort semantics mean the
        // result must still be a success.
        harness.Triggers
            .DispatchAsync(Arg.Any<NotificationTriggerKind>(), Arg.Any<NotificationTriggerPayload>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("dispatcher boom"));

        var dto = new EmitNewDecisionInputDto(KnownTemplateCode, null, null);
        var result = await harness.Service.EmitNewDecisionAsync(DossierSqid, dto);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentId.Should().Be(NewDocumentSqid);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-doce-emit-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long AppId, long SolicitantId);

    private sealed class StubTemplate(string code) : IDocxTemplate
    {
        public string TemplateCode => code;
        public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
            => Result<byte[]>.Success(Array.Empty<byte>());
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DocumentExaminationService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required IDocumentGenerationService DocGen { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required INotificationTriggerDispatcher Triggers { get; init; }

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
            docgen.GenerateDecisionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<string>.Success(NewDocumentSqid)));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(["cnas-examiner"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-emit-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var triggers = Substitute.For<INotificationTriggerDispatcher>();
            triggers.DispatchAsync(
                    Arg.Any<NotificationTriggerKind>(),
                    Arg.Any<NotificationTriggerPayload>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var templates = new IDocxTemplate[]
            {
                new StubTemplate(KnownTemplateCode),
                new StubTemplate("refuz-aplicare"),
            };

            var service = new DocumentExaminationService(
                db, sqids, clock, caller, docgen, audit, notify, mcabinet,
                NullLogger<DocumentExaminationService>.Instance,
                triggers,
                templates);

            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                DocGen = docgen,
                Caller = caller,
                Sqids = sqids,
                Triggers = triggers,
            };
        }

        public async Task<SeedResult> SeedAsync(long? assignedExaminerId)
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
                Code = "SP-EMIT",
                NameRo = "Emit decision test",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-EMIT",
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
                ReferenceNumber = "PS-EMIT-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-EMIT0001",
                AssignedExaminerId = assignedExaminerId,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));
            return new SeedResult(dossier.Id, app.Id, solicitant.Id);
        }
    }
}
