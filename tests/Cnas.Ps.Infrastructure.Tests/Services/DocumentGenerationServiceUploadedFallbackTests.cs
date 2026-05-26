using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// UC17 phase 2B — Integration tests asserting that
/// <see cref="DocumentGenerationService"/> dispatches uploaded templates through the
/// new <see cref="IUploadedTemplateRenderer"/> fallback path while leaving the
/// 35 DI-baked <see cref="Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate"/>
/// path unchanged (regression-guarded). The existing
/// <see cref="DocumentGenerationServiceTests"/> covers the dossier-centric
/// generators; this suite focuses on the new
/// <c>GenerateFromUploadedTemplateAsync</c> overload.
/// </summary>
public class DocumentGenerationServiceUploadedFallbackTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical DOCX MIME — mirrors TemplateAdminService.</summary>
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // ─────────────────────── Dispatch tests ───────────────────────

    /// <summary>
    /// Regression guard — the existing dossier-PDF generation path is unchanged by
    /// the phase 2B fallback wiring. A new collaborator was added but the typed
    /// generators must continue to behave exactly as before. Mirrors the happy-path
    /// assertion from <see cref="DocumentGenerationServiceTests"/>.
    /// </summary>
    [Fact]
    public async Task GenerateCalculationSheetAsync_PdfPath_StillWorks_AfterFallbackAdded()
    {
        var harness = await Harness.CreateWithSeedAsync();

        var result = await harness.Service.GenerateCalculationSheetAsync("DOSS-SQID");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateFromUploadedTemplateAsync_UnknownCode_NoUpload_ReturnsNotFound()
    {
        var harness = await Harness.CreateWithSeedAsync();

        var result = await harness.Service.GenerateFromUploadedTemplateAsync(
            "no-such-template",
            new Dictionary<string, string>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GenerateFromUploadedTemplateAsync_UploadedTemplateCode_UsesRendererFallback()
    {
        // Arrange — seed a persistent template row whose binary contains a {{name}}
        // placeholder. The fallback path must resolve it through the renderer and
        // return the substituted bytes.
        var harness = await Harness.CreateWithSeedAsync();
        var bytes = BuildDocxWithSingleParagraph("Bună, {{name}}!");
        await harness.SeedUploadedAsync("custom-greeting", bytes);

        // Act
        var result = await harness.Service.GenerateFromUploadedTemplateAsync(
            "custom-greeting",
            new Dictionary<string, string> { ["name"] = "Vasile" });

        // Assert — render succeeded and the rendered bytes contain the substituted value.
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("Vasile");
        text.Should().NotContain("{{name}}");
    }

    [Fact]
    public async Task GenerateFromUploadedTemplateAsync_NullData_TreatedAsEmptyDict_LeavesPlaceholdersVerbatim()
    {
        // Arrange — placeholder present, but caller did not supply any data.
        var harness = await Harness.CreateWithSeedAsync();
        var bytes = BuildDocxWithSingleParagraph("Hello {{unknown}}!");
        await harness.SeedUploadedAsync("with-placeholder", bytes);

        // Act — null data dict.
        var result = await harness.Service.GenerateFromUploadedTemplateAsync(
            "with-placeholder",
            data: null);

        // Assert — render succeeded; placeholder left in place.
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("{{unknown}}");
    }

    // ─────────────────────── Test infrastructure ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-docgen-fallback-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock used by the harness.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Synthesises a minimal DOCX whose body contains exactly one paragraph carrying
    /// a single run with the supplied text. Re-used across the dispatch tests as a
    /// template binary the renderer can substitute against.
    /// </summary>
    private static byte[] BuildDocxWithSingleParagraph(string text)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(
            ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();
            var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            body.AppendChild(new Paragraph(run));
            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Re-opens the rendered DOCX and concatenates every text descendant into one
    /// string. Identical shape to the helper in
    /// <see cref="UploadedTemplateRendererTests"/> — kept local so each test file
    /// reads end-to-end without cross-file traversal.
    /// </summary>
    private static string ExtractBodyText(byte[] docxBytes)
    {
        using var ms = new MemoryStream(docxBytes, writable: false);
        using var package = WordprocessingDocument.Open(ms, isEditable: false);
        var body = package.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var t in body.Descendants<Text>())
        {
            sb.Append(t.Text);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wires DocumentGenerationService against an in-memory DB, in-memory file
    /// storage, an NSubstitute audit/caller/sqid/engine, and the real
    /// <see cref="UploadedTemplateRenderer"/>. The renderer takes the same
    /// <see cref="ICnasDbContext"/> + <see cref="IFileStorage"/> as the service so
    /// the persistent row + bytes seeded via <see cref="SeedUploadedAsync"/> are
    /// reachable from both ends of the dispatch.
    /// </summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required InMemoryStorage Storage { get; init; }
        public required DocumentGenerationService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        public static async Task<Harness> CreateWithSeedAsync()
        {
            var db = CreateContext();
            var storage = new InMemoryStorage();

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);

            var fileStorage = (IFileStorage)storage;
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

            var engine = Substitute.For<IDecisionEngine>();
            engine.Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
                .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                    IsEligible: true,
                    Amount: Cnas.Ps.Core.ValueObjects.Money.Mdl(1000m),
                    ReasonCodes: ["OK"],
                    ComputedValues: new Dictionary<string, object?>())));

            // Real renderer — same DB + storage as the service so persistent rows
            // are visible to both. This is the production wiring (both registered
            // Scoped).
            var renderer = new UploadedTemplateRenderer(
                db,
                fileStorage,
                NullLogger<UploadedTemplateRenderer>.Instance);

            var service = new DocumentGenerationService(
                db, sqids, clock, caller, fileStorage, audit, engine,
                NullLogger<DocumentGenerationService>.Instance,
                templates: null,
                uploadedRenderer: renderer);

            // Seed a dossier graph so the dossier-centric regression test has
            // something to render against.
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
                NameRo = "Indemnizație test",
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
                FormPayloadJson = """{"isInsured":true,"birthOrder":1}""",
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

            sqids.TryDecode("DOSS-SQID").Returns(Result<long>.Success(dossier.Id));

            return new Harness
            {
                Db = db,
                Storage = storage,
                Service = service,
                Sqids = sqids,
            };
        }

        /// <summary>
        /// Seeds a <see cref="DocumentTemplate"/> row referencing the supplied bytes,
        /// which are written to the in-memory storage substitute under a fresh
        /// object key. The renderer fallback path resolves the row via the DB,
        /// then fetches the bytes via storage.
        /// </summary>
        public async Task SeedUploadedAsync(string code, byte[] bytes)
        {
            var put = await Storage.PutAsync(
                "cnas-templates",
                new MemoryStream(bytes),
                DocxContentType);
            put.IsSuccess.Should().BeTrue();

            Db.DocumentTemplates.Add(new DocumentTemplate
            {
                Code = code,
                Name = code,
                Version = 1,
                IsCurrent = true,
                StorageObjectKey = put.Value.ObjectKey,
                ContentType = DocxContentType,
                ContentLength = bytes.LongLength,
                ContentSha256 = put.Value.ContentSha256Hex,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// In-memory file-storage substitute. Same shape as the helper in
    /// <see cref="UploadedTemplateRendererTests"/>; kept local to this test file
    /// so each suite reads top-to-bottom without cross-file navigation.
    /// </summary>
    private sealed class InMemoryStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        private static string Key(string bucket, string key) => $"{bucket}::{key}";

        public async Task<Result<StoredObject>> PutAsync(
            string bucket,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var bytes = ms.ToArray();
            var objectKey = $"inmem/{Guid.NewGuid():N}";
            _objects[Key(bucket, objectKey)] = bytes;
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            return Result<StoredObject>.Success(new StoredObject(objectKey, sha, bytes.LongLength));
        }

        public Task<Result<Stream>> GetAsync(
            string bucket,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            if (_objects.TryGetValue(Key(bucket, objectKey), out var bytes))
            {
                return Task.FromResult(Result<Stream>.Success((Stream)new MemoryStream(bytes)));
            }
            return Task.FromResult(Result<Stream>.Failure(
                ErrorCodes.FileUnavailable, "Not found in test storage."));
        }

        public Task<Result<Uri>> PresignDownloadAsync(
            string bucket,
            string objectKey,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<Uri>.Success(new Uri($"inmemory://{bucket}/{objectKey}")));

        public Task<Result> DeleteAsync(
            string bucket,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            _objects.Remove(Key(bucket, objectKey));
            return Task.FromResult(Result.Success());
        }
    }
}
