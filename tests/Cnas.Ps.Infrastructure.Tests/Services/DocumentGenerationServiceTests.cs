using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="DocumentGenerationService"/> (UC08 — auto-draft generation).
/// Uses EF Core InMemory for persistence and NSubstitute for the surrounding collaborators
/// (sqid, clock, caller, storage, audit, engine). The file-storage substitute returns a
/// canned <see cref="StoredObject"/> so the tests don't require a running MinIO instance.
/// </summary>
public class DocumentGenerationServiceTests
{
    /// <summary>Deterministic clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical Sqid string used to map to the seeded dossier.</summary>
    private const string DossierSqid = "DOSS-SQID";

    [Fact]
    public async Task GenerateCalculationSheetAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.GenerateCalculationSheetAsync("bad");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task GenerateCalculationSheetAsync_DossierNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.GenerateCalculationSheetAsync("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GenerateCalculationSheetAsync_HappyPath_StoresPdfAndDocumentRow_AuditsNotice()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateCalculationSheetAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();

        // PDF uploaded via IFileStorage exactly once with application/pdf content type.
        await harness.Storage.Received(1).PutAsync(
            "cnas-documents",
            Arg.Any<Stream>(),
            "application/pdf",
            Arg.Any<CancellationToken>());

        // A Document row exists referencing the dossier with Kind=Information for calc sheets.
        var docs = await harness.Db.Documents.Where(d => d.DossierId == seeded.DossierId).ToListAsync();
        docs.Should().ContainSingle();
        var doc = docs[0];
        doc.Kind.Should().Be(DocumentKind.Information);
        doc.MimeType.Should().Be("application/pdf");
        doc.StorageBucket.Should().Be("cnas-documents");
        doc.SizeBytes.Should().BeGreaterThan(0);
        doc.ContentSha256Hex.Should().NotBeNullOrWhiteSpace();

        await harness.Audit.Received(1).RecordAsync(
            "DOCUMENT.GENERATED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            "Document",
            doc.Id,
            Arg.Is<string>(s => s.Contains("CalculationSheet", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateDecisionAsync_HappyPath_StoresPdfAndDocumentRow()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateDecisionAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();

        var docs = await harness.Db.Documents.Where(d => d.DossierId == seeded.DossierId).ToListAsync();
        docs.Should().ContainSingle();
        var doc = docs[0];
        doc.Kind.Should().Be(DocumentKind.Decision);
        doc.Title.Should().StartWith("Decizia_");
        doc.Title.Should().EndWith(".pdf");

        await harness.Audit.Received(1).RecordAsync(
            "DOCUMENT.GENERATED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            "Document",
            doc.Id,
            Arg.Is<string>(s => s.Contains("Decision", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateDecisionAsync_StorageFailure_PropagatesFailure()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();
        // Make the storage backend refuse the upload.
        harness.Storage.PutAsync(
                Arg.Any<string>(),
                Arg.Any<Stream>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                Result<StoredObject>.Failure(ErrorCodes.FileUnavailable, "MinIO down")));

        var result = await harness.Service.GenerateDecisionAsync(DossierSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FileUnavailable);

        // No Document row should have been written.
        (await harness.Db.Documents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GenerateCalculationSheetAsync_FilenameContainsDossierNumber()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateCalculationSheetAsync(DossierSqid);

        result.IsSuccess.Should().BeTrue();
        var doc = await harness.Db.Documents.SingleAsync(d => d.DossierId == seeded.DossierId);
        doc.Title.Should().StartWith("Fisa-de-calcul_");
        doc.Title.Should().Contain(seeded.DossierNumber);
        doc.Title.Should().EndWith(".pdf");
    }

    // ──────────────── DOCX rendering tests (Annex 7) ────────────────

    /// <summary>
    /// Asserts the DOCX render path produces a stream whose first 4 bytes match the
    /// ZIP local-file-header magic ("PK\x03\x04"). DOCX is a ZIP envelope, so this is
    /// the cheapest, most format-agnostic way to confirm we shipped a valid Word file.
    /// </summary>
    [Fact]
    public async Task GenerateCalculationSheetAsync_DocxFormat_ProducesDocxWithCorrectMagicBytes()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        // Capture the byte stream that DocumentGenerationService hands to storage.
        byte[]? capturedBytes = null;
        harness.Storage.PutAsync(
                Arg.Any<string>(),
                Arg.Do<Stream>(s =>
                {
                    using var ms = new MemoryStream();
                    if (s.CanSeek)
                    {
                        s.Position = 0;
                    }
                    s.CopyTo(ms);
                    capturedBytes = ms.ToArray();
                }),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                Result<StoredObject>.Success(new StoredObject(
                    ObjectKey: "test-key",
                    ContentSha256Hex: new string('a', 64),
                    SizeBytes: capturedBytes?.LongLength ?? 0L))));

        var result = await harness.Service.GenerateCalculationSheetAsync(DossierSqid, DocumentRenderFormat.Docx);

        result.IsSuccess.Should().BeTrue();
        capturedBytes.Should().NotBeNull();
        capturedBytes!.Length.Should().BeGreaterThan(4);

        // ZIP local-file-header signature — DOCX is a ZIP envelope.
        capturedBytes[0].Should().Be(0x50);
        capturedBytes[1].Should().Be(0x4B);
        capturedBytes[2].Should().Be(0x03);
        capturedBytes[3].Should().Be(0x04);
    }

    [Fact]
    public async Task GenerateCalculationSheetAsync_DocxFormat_FilenameEndsWithDocx()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateCalculationSheetAsync(DossierSqid, DocumentRenderFormat.Docx);

        result.IsSuccess.Should().BeTrue();
        var doc = await harness.Db.Documents.SingleAsync(d => d.DossierId == seeded.DossierId);
        doc.Title.Should().StartWith("Fisa-de-calcul_");
        doc.Title.Should().Contain(seeded.DossierNumber);
        doc.Title.Should().EndWith(".docx");
    }

    [Fact]
    public async Task GenerateDecisionAsync_DocxFormat_ContentTypeIsOpenXmlWord()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateDecisionAsync(DossierSqid, DocumentRenderFormat.Docx);

        result.IsSuccess.Should().BeTrue();
        var doc = await harness.Db.Documents.SingleAsync(d => d.DossierId == seeded.DossierId);
        doc.MimeType.Should().Be(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        doc.Title.Should().StartWith("Decizia_");
        doc.Title.Should().EndWith(".docx");

        // Storage was called with the OpenXML MIME type.
        await harness.Storage.Received(1).PutAsync(
            "cnas-documents",
            Arg.Any<Stream>(),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression guard — the PDF default overload still produces a PDF after the
    /// DocumentRenderFormat refactor (backward compatibility).
    /// </summary>
    [Fact]
    public async Task GenerateCalculationSheetAsync_PdfFormat_BackwardCompatible_StillProducesPdf()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.GenerateCalculationSheetAsync(DossierSqid, DocumentRenderFormat.Pdf);

        result.IsSuccess.Should().BeTrue();
        var doc = await harness.Db.Documents.SingleAsync(d => d.DossierId == seeded.DossierId);
        doc.MimeType.Should().Be("application/pdf");
        doc.Title.Should().EndWith(".pdf");
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-docgen-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long AppId, long SolicitantId, long PassportId, string DossierNumber);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DocumentGenerationService Service { get; init; }
        public required IFileStorage Storage { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }
        public required IDecisionEngine Engine { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var storage = Substitute.For<IFileStorage>();
            // Default: storage accepts the upload and returns a canned object descriptor.
            storage.PutAsync(
                    Arg.Any<string>(),
                    Arg.Any<Stream>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    Result<StoredObject>.Success(
                        new StoredObject(
                            ObjectKey: $"2026/05/19/{Guid.NewGuid():N}",
                            ContentSha256Hex: new string('a', 64),
                            SizeBytes: ConsumeStreamLength(call.Arg<Stream>())))));

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
            caller.CorrelationId.Returns("corr-1");

            // Real engine — keep the test honest about the engine collaboration but supply
            // a passport with a rule-set the engine evaluates without throwing.
            var engine = Substitute.For<IDecisionEngine>();
            engine.Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
                .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                    IsEligible: true,
                    Amount: Money.Mdl(1000m),
                    ReasonCodes: ["BIRTH_GRANT_ELIGIBLE"],
                    ComputedValues: new Dictionary<string, object?>())));

            var service = new DocumentGenerationService(
                db, sqids, clock, caller, storage, audit, engine, NullLogger<DocumentGenerationService>.Instance);
            return new Harness
            {
                Db = db,
                Service = service,
                Storage = storage,
                Audit = audit,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
                Engine = engine,
            };
        }

        /// <summary>Reads the stream to expose a non-zero size on the canned StoredObject.</summary>
        private static long ConsumeStreamLength(Stream stream)
        {
            if (!stream.CanRead) return 1024;
            if (stream.CanSeek)
            {
                return stream.Length;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.Length;
        }

        public async Task<SeedResult> SeedAsync()
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
                NameRo = "Indemnizație test",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                DecisionRulesJson = "{\"code\":\"TEST\"}",
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
                FormPayloadJson = """{"isInsured":true,"birthOrder":1}""",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossierNumber = "D-2026-ABCD1234";
            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = dossierNumber,
                AssignedExaminerId = 1L,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));

            return new SeedResult(dossier.Id, app.Id, solicitant.Id, passport.Id, dossierNumber);
        }
    }
}
