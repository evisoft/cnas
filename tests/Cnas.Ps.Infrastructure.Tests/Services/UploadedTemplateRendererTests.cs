using System.Text;
using Cnas.Ps.Application.Abstractions;
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
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="UploadedTemplateRenderer"/> (UC17 phase 2B — uploaded
/// template renderer). Exercises the placeholder-substitution contract against
/// synthesised OpenXML inputs so the assertions are about the substitution algorithm
/// itself, not about any particular hand-authored Word document.
/// </summary>
/// <remarks>
/// <para>
/// Every test builds the input DOCX directly via <see cref="WordprocessingDocument"/>
/// to keep the assertion subjects narrow: a paragraph with one run vs a paragraph
/// whose <c>{{name}}</c> placeholder is split across multiple runs (Word does this
/// silently when formatting changes mid-token), tables, and so on. The output is
/// re-opened with the same API and walked for the expected text so the round-trip
/// path stays end-to-end inside OpenXML rather than relying on text-based byte
/// inspection.
/// </para>
/// </remarks>
public class UploadedTemplateRendererTests
{
    /// <summary>Canonical DOCX MIME used by every test in this suite.</summary>
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // ─────────────────────── CanRenderAsync ───────────────────────

    [Fact]
    public async Task CanRenderAsync_KnownCode_ReturnsTrue()
    {
        var db = CreateContext();
        var storage = new InMemoryStorage();
        await SeedRowAsync(db, storage, "known-code", new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        var result = await sut.CanRenderAsync("known-code");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanRenderAsync_UnknownCode_ReturnsFalse()
    {
        var db = CreateContext();
        var sut = new UploadedTemplateRenderer(db, new InMemoryStorage(), NullLogger<UploadedTemplateRenderer>.Instance);

        var result = await sut.CanRenderAsync("does-not-exist");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanRenderAsync_InactiveRow_ReturnsFalse()
    {
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "soft-deleted",
            Name = "Soft Deleted",
            Version = 1,
            IsCurrent = true,
            StorageObjectKey = "templates/soft-deleted/v1/soft-deleted.docx",
            ContentType = DocxContentType,
            ContentLength = 4,
            ContentSha256 = "0".PadRight(64, '0'),
            CreatedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            IsActive = false,
        });
        await db.SaveChangesAsync();

        var sut = new UploadedTemplateRenderer(db, new InMemoryStorage(), NullLogger<UploadedTemplateRenderer>.Instance);

        var result = await sut.CanRenderAsync("soft-deleted");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanRenderAsync_NotCurrentRow_ReturnsFalse()
    {
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "superseded",
            Name = "Superseded",
            Version = 1,
            IsCurrent = false,
            StorageObjectKey = "templates/superseded/v1/superseded.docx",
            ContentType = DocxContentType,
            ContentLength = 4,
            ContentSha256 = "0".PadRight(64, '0'),
            CreatedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var sut = new UploadedTemplateRenderer(db, new InMemoryStorage(), NullLogger<UploadedTemplateRenderer>.Instance);

        var result = await sut.CanRenderAsync("superseded");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanRenderAsync_NullOrWhitespace_ReturnsFalse()
    {
        var db = CreateContext();
        var sut = new UploadedTemplateRenderer(db, new InMemoryStorage(), NullLogger<UploadedTemplateRenderer>.Instance);

        (await sut.CanRenderAsync(null!)).Should().BeFalse();
        (await sut.CanRenderAsync("")).Should().BeFalse();
        (await sut.CanRenderAsync("   ")).Should().BeFalse();
    }

    [Fact]
    public async Task CanRenderAsync_CaseInsensitiveMatch_ReturnsTrue()
    {
        // The code is persisted lower-case; CanRenderAsync should canonicalise the input
        // before the lookup so admin clients sending "MY-CODE" still resolve the row
        // stored as "my-code".
        var db = CreateContext();
        var storage = new InMemoryStorage();
        await SeedRowAsync(db, storage, "my-code", new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        (await sut.CanRenderAsync("MY-CODE")).Should().BeTrue();
        (await sut.CanRenderAsync("  My-Code  ")).Should().BeTrue();
    }

    // ─────────────────────── RenderAsync ───────────────────────

    [Fact]
    public async Task RenderAsync_SimplePlaceholder_SubstitutesValue()
    {
        // Arrange — a DOCX whose single paragraph carries one run with "{{name}}".
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithSingleParagraph("Hello {{name}}, welcome!");
        await SeedRowAsync(db, storage, "greeting", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "greeting",
            new Dictionary<string, string> { ["name"] = "Ion Popescu" });

        // Assert
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("Ion Popescu");
        text.Should().NotContain("{{name}}");
    }

    [Fact]
    public async Task RenderAsync_MultiRunPlaceholder_StillSubstitutes()
    {
        // Arrange — synthesise a paragraph where the placeholder spans three runs:
        // "{{", "name", "}}". This is the shape Word produces when formatting changes
        // mid-token; without a paragraph-level pass the substitution would miss the
        // marker entirely.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithMultiRunParagraph("{{", "name", "}}");
        await SeedRowAsync(db, storage, "multi-run", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "multi-run",
            new Dictionary<string, string> { ["name"] = "Maria" });

        // Assert
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("Maria");
        text.Should().NotContain("{{");
        text.Should().NotContain("}}");
    }

    [Fact]
    public async Task RenderAsync_UnknownPlaceholder_LeftVerbatim()
    {
        // Arrange — placeholder present in the document but absent from the data dict.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithSingleParagraph("Hello {{unknown_key}}!");
        await SeedRowAsync(db, storage, "unknown-placeholder", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "unknown-placeholder",
            new Dictionary<string, string> { ["other_key"] = "ignored" });

        // Assert — placeholder is left in place (no exception, no failure).
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("{{unknown_key}}");
    }

    [Fact]
    public async Task RenderAsync_MissingTemplate_ReturnsNotFound()
    {
        var db = CreateContext();
        var sut = new UploadedTemplateRenderer(db, new InMemoryStorage(), NullLogger<UploadedTemplateRenderer>.Instance);

        var result = await sut.RenderAsync(
            "does-not-exist",
            new Dictionary<string, string>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task RenderAsync_TablePlaceholder_Substitutes()
    {
        // Arrange — a single-row table with one cell carrying the placeholder. Table
        // cells use the same <w:p> shape as body paragraphs, so the paragraph-level
        // pass must visit every paragraph inside every cell.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithTableCell("Status: {{status}}");
        await SeedRowAsync(db, storage, "table", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "table",
            new Dictionary<string, string> { ["status"] = "Approved" });

        // Assert
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("Approved");
        text.Should().NotContain("{{status}}");
    }

    [Fact]
    public async Task RenderAsync_EmptyDictionary_LeavesAllPlaceholdersVerbatim()
    {
        // Arrange — two placeholders in one paragraph; empty data dict.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithSingleParagraph("{{first}} and {{second}}");
        await SeedRowAsync(db, storage, "empty-dict", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "empty-dict",
            new Dictionary<string, string>());

        // Assert — both placeholders survive verbatim.
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("{{first}}");
        text.Should().Contain("{{second}}");
    }

    [Fact]
    public async Task RenderAsync_MultipleDistinctPlaceholders_SubstitutesAll()
    {
        // Arrange — three placeholders in one paragraph; all in the data dict.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var docx = BuildDocxWithSingleParagraph("From {{from}} to {{to}} on {{date}}.");
        await SeedRowAsync(db, storage, "multi", docx);

        var sut = new UploadedTemplateRenderer(db, storage, NullLogger<UploadedTemplateRenderer>.Instance);

        // Act
        var result = await sut.RenderAsync(
            "multi",
            new Dictionary<string, string>
            {
                ["from"] = "Chișinău",
                ["to"] = "Bălți",
                ["date"] = "2026-05-20",
            });

        // Assert
        result.IsSuccess.Should().BeTrue();
        var text = ExtractBodyText(result.Value);
        text.Should().Contain("Chișinău");
        text.Should().Contain("Bălți");
        text.Should().Contain("2026-05-20");
        text.Should().NotContain("{{");
    }

    // ─────────────────────── Test infrastructure ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-uploadedrender-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Persists a <see cref="DocumentTemplate"/> row and uploads the supplied bytes to
    /// the in-memory storage substitute. Returns the storage key actually used (the
    /// caller usually doesn't need it but it is convenient for diagnostic logging).
    /// </summary>
    private static async Task SeedRowAsync(
        CnasDbContext db,
        InMemoryStorage storage,
        string code,
        byte[] bytes)
    {
        var put = await storage.PutAsync(
            "cnas-templates",
            new MemoryStream(bytes),
            DocxContentType);
        put.IsSuccess.Should().BeTrue();

        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = code,
            Name = code,
            Version = 1,
            IsCurrent = true,
            StorageObjectKey = put.Value.ObjectKey,
            ContentType = DocxContentType,
            ContentLength = bytes.LongLength,
            ContentSha256 = put.Value.ContentSha256Hex,
            CreatedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Synthesises a minimal DOCX whose body contains exactly one paragraph carrying a
    /// single <c>&lt;w:r&gt;&lt;w:t&gt;</c> pair with the supplied text. Used by the
    /// happy-path tests so the placeholder lives in a known shape.
    /// </summary>
    private static byte[] BuildDocxWithSingleParagraph(string text)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
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
    /// Synthesises a minimal DOCX whose body contains exactly one paragraph with the
    /// supplied run fragments appended in order. Used by the multi-run test to prove
    /// the substitution survives a placeholder that spans multiple
    /// <c>&lt;w:r&gt;</c> elements (the shape Word emits when formatting changes
    /// mid-token).
    /// </summary>
    private static byte[] BuildDocxWithMultiRunParagraph(params string[] fragments)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();
            var paragraph = new Paragraph();
            foreach (var fragment in fragments)
            {
                paragraph.AppendChild(new Run(new Text(fragment) { Space = SpaceProcessingModeValues.Preserve }));
            }
            body.AppendChild(paragraph);
            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Synthesises a minimal DOCX whose body contains a one-row, one-cell table whose
    /// only paragraph carries the supplied text. Used by the table test to prove the
    /// renderer descends into table cells (not just direct-body paragraphs).
    /// </summary>
    private static byte[] BuildDocxWithTableCell(string text)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();
            var table = new Table();
            var row = new TableRow();
            var cell = new TableCell(new Paragraph(
                new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            row.AppendChild(cell);
            table.AppendChild(row);
            body.AppendChild(table);
            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Re-opens the rendered DOCX and concatenates the body's text content into a
    /// single string. Visits paragraphs both at the body level and inside tables so
    /// the assertion catches substitutions in either location.
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
    /// Minimal in-memory <see cref="IFileStorage"/> stub. Mirrors the shape of the E2E
    /// fixture's <c>InMemoryFileStorage</c> but kept local to the unit-test assembly so
    /// the Infrastructure suite does not take a dependency on the E2E project.
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
            var objectKey = $"unit/{Guid.NewGuid():N}";
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
