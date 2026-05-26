using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Integration tests for the template-routing behaviour of <see cref="DocumentGenerationService"/>:
/// when a known <see cref="IDocxTemplate.TemplateCode"/> is registered, the dispatcher must
/// invoke it; when no template matches, the dispatcher must fall back to the generic
/// in-class <c>BuildDocx</c> path. Both must produce structurally-valid DOCX bytes.
/// </summary>
public class DocumentGenerationServiceTemplateRoutingTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string DossierSqid = "DOSS-SQID";

    /// <summary>A test-only template that records the calls it receives.</summary>
    private sealed class RecordingTemplate(string code) : IDocxTemplate
    {
        public string TemplateCode { get; } = code;

        /// <summary>How many times <see cref="Render"/> was invoked.</summary>
        public int CallCount { get; private set; }

        public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
        {
            CallCount++;
            // A minimal valid ZIP-magic-byte stub is enough — the routing test does not
            // open the DOCX, it only verifies the routing decision.
            return Result<byte[]>.Success(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF });
        }
    }

    [Fact]
    public async Task GenerateDecisionAsync_DocxFormat_KnownTemplateRegistered_RoutesToTemplate()
    {
        // Register a template whose code matches the decision Annex 7 code.
        var template = new RecordingTemplate(DeciziaPensieTemplate.Code);
        var harness = await CreateHarnessAsync(template);

        var result = await harness.Service.GenerateDecisionAsync(DossierSqid, DocumentRenderFormat.Docx);

        result.IsSuccess.Should().BeTrue();
        template.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateDecisionAsync_DocxFormat_NoTemplateRegistered_FallsBackToGenericBuilder()
    {
        // Register a template with a code that intentionally does NOT match either Annex 7
        // code so the dispatcher must fall through to the generic BuildDocx.
        var template = new RecordingTemplate("unknown-template-code");
        var harness = await CreateHarnessAsync(template);

        var result = await harness.Service.GenerateDecisionAsync(DossierSqid, DocumentRenderFormat.Docx);

        result.IsSuccess.Should().BeTrue();
        template.CallCount.Should().Be(0);
    }

    // ───────────────────────────── harness ─────────────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(DocumentGenerationService Service);

    private static async Task<Harness> CreateHarnessAsync(params IDocxTemplate[] templates)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-docgen-route-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new CnasDbContext(opts);

        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = "2000000000007",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);

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
            DecisionRulesJson = "{\"code\":\"TEST\"}",
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.UnderExamination,
            FormPayloadJson = """{"isInsured":true}""",
            SnapshotJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-1),
            ReferenceNumber = "PS-TEST-0001",
            IsActive = true,
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();

        var dossier = new Dossier
        {
            CreatedAtUtc = ClockNow,
            ApplicationId = app.Id,
            DossierNumber = "D-2026-ABCD1234",
            AssignedExaminerId = 1L,
            IsActive = true,
        };
        db.Dossiers.Add(dossier);
        await db.SaveChangesAsync();

        sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));

        var storage = Substitute.For<IFileStorage>();
        storage.PutAsync(
                Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StoredObject>.Success(
                new StoredObject("k", new string('a', 64), 1024L))));

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        caller.UserId.Returns(1L);
        caller.Roles.Returns(["cnas-examiner"]);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-route");

        var engine = Substitute.For<IDecisionEngine>();
        engine.Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: true,
                Amount: Money.Mdl(1000m),
                ReasonCodes: ["OK"],
                ComputedValues: new Dictionary<string, object?>())));

        var service = new DocumentGenerationService(
            db,
            sqids,
            new StubClock(ClockNow),
            caller,
            storage,
            audit,
            engine,
            NullLogger<DocumentGenerationService>.Instance,
            templates);
        return new Harness(service);
    }
}
