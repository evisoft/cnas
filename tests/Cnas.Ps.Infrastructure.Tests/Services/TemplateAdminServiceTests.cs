using System.Security.Cryptography;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TemplateAdminService"/> (UC17 — template admin surface).
/// Phase 1 covered the read-only projection over the DI-baked
/// <see cref="IDocxTemplate"/> singletons; phase 2A adds the persistent
/// <c>DocumentTemplates</c> table + MinIO upload/download path and the union-with-
/// collision-tiebreak between the two sources. Tests construct the SUT with a hand-rolled
/// list of fake DI templates plus an EF InMemory context and an in-memory file-storage
/// substitute so the assertion subject stays the service's projection logic, not the
/// underlying infrastructure.
/// </summary>
public class TemplateAdminServiceTests
{
    /// <summary>Deterministic clock instant used by every test in this suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    /// <param name="templates">Fake DI-baked template instances.</param>
    /// <param name="db">EF Core context backing the persistent half of the catalog.</param>
    /// <param name="storage">File storage adapter (in-memory in tests).</param>
    /// <returns>Fully wired <see cref="TemplateAdminService"/>.</returns>
    private static TemplateAdminService NewSut(
        IEnumerable<IDocxTemplate> templates,
        CnasDbContext db,
        IFileStorage storage)
        => new(templates, db, storage, new StubClock(ClockNow));

    /// <summary>Shorthand for the phase-1-equivalent SUT (no persistent rows seeded).</summary>
    private static TemplateAdminService NewSut(params IDocxTemplate[] templates)
    {
        var db = CreateContext();
        return new TemplateAdminService(templates, db, new InMemoryStorage(), new StubClock(ClockNow));
    }

    /// <summary>
    /// Stand-in <see cref="IDocxTemplate"/> used by every unit test. <see cref="Render"/> is
    /// never called by the admin surface (it is read-only) — we throw so any accidental
    /// use is loud rather than silently producing a successful zero-byte payload.
    /// </summary>
    private sealed class FakeTemplate(string code) : IDocxTemplate
    {
        /// <inheritdoc />
        public string TemplateCode { get; } = code;

        /// <inheritdoc />
        public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
            => throw new NotSupportedException(
                "FakeTemplate.Render should never be invoked by the template-admin surface.");
    }

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task ListAsync_ReturnsAllRegistered_SortedByCode()
    {
        var sut = NewSut(
            new FakeTemplate("zebra"),
            new FakeTemplate("alpha"),
            new FakeTemplate("mango"));

        var result = await sut.ListAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(e => e.Code).Should().ContainInOrder("alpha", "mango", "zebra");
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListAsync_Empty_Returns_EmptyList()
    {
        var sut = NewSut(/* none */);

        var result = await sut.ListAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_PopulatesClrTypeAndAssemblyMetadata()
    {
        var sut = NewSut(new FakeTemplate("alpha"));

        var result = await sut.ListAsync();

        result.IsSuccess.Should().BeTrue();
        var entry = result.Value.Single();
        entry.Code.Should().Be("alpha");
        entry.ClrTypeFullName.Should().NotBeNullOrWhiteSpace();
        entry.ClrTypeFullName.Should().Contain("FakeTemplate");
        entry.AssemblyName.Should().NotBeNullOrWhiteSpace();
        // Phase 2A — Source field defaults to "DI" for DI-baked rows.
        entry.Source.Should().Be("DI");
    }

    [Fact]
    public async Task ListAsync_UnionsDiAndPersistentRows()
    {
        // Arrange — one DI-baked template and one persistent row with a different code.
        // The union should yield both, sorted by code.
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "persistent-one",
            Name = "Persistent One",
            Version = 1,
            IsCurrent = true,
            StorageObjectKey = "templates/persistent-one/v1/persistent-one.docx",
            ContentType = TemplateAdminService.DocxContentType,
            ContentLength = 1234,
            ContentSha256 = "0".PadRight(64, '0'),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var sut = NewSut([new FakeTemplate("di-only")], db, new InMemoryStorage());

        // Act
        var result = await sut.ListAsync();

        // Assert — both codes appear, sorted alphabetically.
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(e => e.Code).Should().ContainInOrder("di-only", "persistent-one");
        result.Value.Single(e => e.Code == "di-only").Source.Should().Be("DI");
        var p = result.Value.Single(e => e.Code == "persistent-one");
        p.Source.Should().Be("Persistent");
        p.Name.Should().Be("Persistent One");
        p.Version.Should().Be(1);
        p.ContentLength.Should().Be(1234);
    }

    [Fact]
    public async Task ListAsync_PersistentOverridesDi_OnCodeCollision()
    {
        // Arrange — same code in both registries; persistent must win.
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "shared",
            Name = "Operator Custom",
            Version = 1,
            IsCurrent = true,
            StorageObjectKey = "templates/shared/v1/shared.docx",
            ContentType = TemplateAdminService.DocxContentType,
            ContentLength = 9999,
            ContentSha256 = "1".PadRight(64, '1'),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var sut = NewSut([new FakeTemplate("shared")], db, new InMemoryStorage());

        // Act
        var result = await sut.ListAsync();

        // Assert — exactly one row with code "shared", and it is the persistent one.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value.Single().Source.Should().Be("Persistent");
        result.Value.Single().Name.Should().Be("Operator Custom");
    }

    [Fact]
    public async Task ListAsync_HistoricalPersistentRows_AreHidden()
    {
        // Arrange — two rows for the same code: v1 (IsCurrent=false) and v2 (IsCurrent=true).
        // The list should only show v2.
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "history",
            Name = "History v1",
            Version = 1,
            IsCurrent = false,
            StorageObjectKey = "templates/history/v1/history.docx",
            ContentType = TemplateAdminService.DocxContentType,
            ContentLength = 100,
            ContentSha256 = "2".PadRight(64, '2'),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "history",
            Name = "History v2",
            Version = 2,
            IsCurrent = true,
            StorageObjectKey = "templates/history/v2/history.docx",
            ContentType = TemplateAdminService.DocxContentType,
            ContentLength = 200,
            ContentSha256 = "3".PadRight(64, '3'),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var sut = NewSut([], db, new InMemoryStorage());

        // Act
        var result = await sut.ListAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value.Single().Version.Should().Be(2);
        result.Value.Single().Name.Should().Be("History v2");
    }

    // ─────────────────────── GetAsync ───────────────────────

    [Fact]
    public async Task GetAsync_KnownCode_ReturnsEntry()
    {
        var sut = NewSut(
            new FakeTemplate("alpha"),
            new FakeTemplate("bravo"));

        var result = await sut.GetAsync("bravo");

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("bravo");
    }

    [Fact]
    public async Task GetAsync_UnknownCode_ReturnsNotFoundResult()
    {
        var sut = NewSut(new FakeTemplate("alpha"));

        var result = await sut.GetAsync("does-not-exist");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetAsync_CodeIsCaseInsensitive()
    {
        var sut = NewSut(new FakeTemplate("refuz-aplicare"));

        var result = await sut.GetAsync("REFUZ-APLICARE");

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("refuz-aplicare");
    }

    [Fact]
    public async Task GetAsync_NullOrEmptyCode_ReturnsNotFoundResult()
    {
        var sut = NewSut(new FakeTemplate("alpha"));

        var nullResult = await sut.GetAsync(null!);
        var emptyResult = await sut.GetAsync("");
        var wsResult = await sut.GetAsync("   ");

        nullResult.IsFailure.Should().BeTrue();
        nullResult.ErrorCode.Should().Be(ErrorCodes.NotFound);
        emptyResult.IsFailure.Should().BeTrue();
        emptyResult.ErrorCode.Should().Be(ErrorCodes.NotFound);
        wsResult.IsFailure.Should().BeTrue();
        wsResult.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetAsync_PersistentRowWinsOverDi_OnCollision()
    {
        // Arrange — both registries carry "collide"; persistent must win.
        var db = CreateContext();
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = "collide",
            Name = "Persistent Wins",
            Version = 7,
            IsCurrent = true,
            StorageObjectKey = "templates/collide/v7/collide.docx",
            ContentType = TemplateAdminService.DocxContentType,
            ContentLength = 55,
            ContentSha256 = "4".PadRight(64, '4'),
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var sut = NewSut([new FakeTemplate("collide")], db, new InMemoryStorage());

        // Act
        var result = await sut.GetAsync("collide");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Source.Should().Be("Persistent");
        result.Value.Name.Should().Be("Persistent Wins");
        result.Value.Version.Should().Be(7);
    }

    // ─────────────────────── UploadAsync ───────────────────────

    [Fact]
    public async Task UploadAsync_ValidDocx_PersistsRowAndReturnsEntry()
    {
        // Arrange — minimal but magic-byte-prefixed DOCX content.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var sut = NewSut([], db, storage);
        var content = BuildDocxBytes(payload: "hello-world");

        // Act
        using var stream = new MemoryStream(content);
        var result = await sut.UploadAsync(
            code: "my-template",
            name: "My Template",
            description: "test upload",
            content: stream,
            contentType: TemplateAdminService.DocxContentType);

        // Assert — success, single row persisted, returned entry shape.
        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("my-template");
        result.Value.Source.Should().Be("Persistent");
        result.Value.Name.Should().Be("My Template");
        result.Value.Version.Should().Be(1);
        result.Value.ContentLength.Should().Be(content.Length);

        var row = await db.DocumentTemplates.SingleAsync();
        row.Code.Should().Be("my-template");
        row.Name.Should().Be("My Template");
        row.Description.Should().Be("test upload");
        row.Version.Should().Be(1);
        row.IsCurrent.Should().BeTrue();
        row.ContentLength.Should().Be(content.Length);
        row.ContentSha256.Should().Be(Sha256Hex(content));
        row.CreatedAtUtc.Should().Be(ClockNow);

        // Storage holds the bytes under the row's object key.
        storage.Contains(TemplateAdminService.TemplatesBucket, row.StorageObjectKey).Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_SameCodeTwice_BumpsVersionAndFlipsCurrent()
    {
        // Arrange
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var sut = NewSut([], db, storage);
        var v1Bytes = BuildDocxBytes(payload: "version-one");
        var v2Bytes = BuildDocxBytes(payload: "version-two-with-different-bytes");

        // Act — two uploads for the same code.
        using (var s1 = new MemoryStream(v1Bytes))
        {
            (await sut.UploadAsync("dup", "Dup v1", null, s1, TemplateAdminService.DocxContentType))
                .IsSuccess.Should().BeTrue();
        }
        using (var s2 = new MemoryStream(v2Bytes))
        {
            var second = await sut.UploadAsync("dup", "Dup v2", null, s2, TemplateAdminService.DocxContentType);
            second.IsSuccess.Should().BeTrue();
            second.Value.Version.Should().Be(2);
        }

        // Assert — two rows; v1 IsCurrent=false, v2 IsCurrent=true.
        var rows = await db.DocumentTemplates.OrderBy(r => r.Version).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Version.Should().Be(1);
        rows[0].IsCurrent.Should().BeFalse();
        rows[1].Version.Should().Be(2);
        rows[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_NullOrEmptyCode_ReturnsValidationFailed()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        using var s = new MemoryStream(BuildDocxBytes("x"));

        var r1 = await sut.UploadAsync("", "n", null, s, TemplateAdminService.DocxContentType);
        var r2 = await sut.UploadAsync("   ", "n", null, s, TemplateAdminService.DocxContentType);

        r1.IsFailure.Should().BeTrue();
        r1.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        r2.IsFailure.Should().BeTrue();
        r2.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("Has-UpperCase")]
    [InlineData("trailing-")]
    [InlineData("-leading")]
    [InlineData("double--hyphen")]
    [InlineData("under_score")]
    [InlineData("spaces here")]
    public async Task UploadAsync_NonKebabCode_ReturnsValidationFailed(string badCode)
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        using var s = new MemoryStream(BuildDocxBytes("x"));

        var r = await sut.UploadAsync(badCode, "Name", null, s, TemplateAdminService.DocxContentType);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UploadAsync_OversizedCode_ReturnsValidationFailed()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        using var s = new MemoryStream(BuildDocxBytes("x"));
        var tooLong = new string('a', 97);

        var r = await sut.UploadAsync(tooLong, "Name", null, s, TemplateAdminService.DocxContentType);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UploadAsync_NullOrEmptyName_ReturnsValidationFailed()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        using var s = new MemoryStream(BuildDocxBytes("x"));

        var r = await sut.UploadAsync("ok-code", "  ", null, s, TemplateAdminService.DocxContentType);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UploadAsync_WrongMimeType_ReturnsFileTypeMismatch()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        using var s = new MemoryStream(BuildDocxBytes("x"));

        var r = await sut.UploadAsync("ok-code", "Name", null, s, "application/pdf");

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
    }

    [Fact]
    public async Task UploadAsync_MissingMagicBytes_ReturnsFileTypeMismatch()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        // Not a ZIP/DOCX header — first byte is 'X'.
        var bytes = new byte[] { 0x58, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        using var s = new MemoryStream(bytes);

        var r = await sut.UploadAsync("ok-code", "Name", null, s, TemplateAdminService.DocxContentType);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
    }

    [Fact]
    public async Task UploadAsync_OversizedContent_ReturnsFileTooLarge()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());
        // 5 MiB + 1 byte total — exceeds the cap.
        var oversized = new byte[TemplateAdminService.MaxTemplateSize + 1];
        oversized[0] = 0x50;
        oversized[1] = 0x4B;
        oversized[2] = 0x03;
        oversized[3] = 0x04;
        using var s = new MemoryStream(oversized);

        var r = await sut.UploadAsync("ok-code", "Name", null, s, TemplateAdminService.DocxContentType);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.FileTooLarge);
    }

    // ─────────────────────── DownloadAsync ───────────────────────

    [Fact]
    public async Task DownloadAsync_PersistentRow_ReturnsExactBytes()
    {
        // Arrange — upload, then download.
        var db = CreateContext();
        var storage = new InMemoryStorage();
        var sut = NewSut([], db, storage);
        var payload = BuildDocxBytes("download-me");
        using (var s = new MemoryStream(payload))
        {
            (await sut.UploadAsync("dl", "DL", null, s, TemplateAdminService.DocxContentType))
                .IsSuccess.Should().BeTrue();
        }

        // Act
        var result = await sut.DownloadAsync("dl");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be(TemplateAdminService.DocxContentType);
        result.Value.ContentLength.Should().Be(payload.Length);
        result.Value.SuggestedFileName.Should().Be("dl.docx");
        result.Value.Sha256.Should().Be(Sha256Hex(payload));

        using var ms = new MemoryStream();
        await result.Value.Content.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task DownloadAsync_UnknownCode_ReturnsNotFound()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());

        var r = await sut.DownloadAsync("nope");

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task DownloadAsync_DiBakedOnly_ReturnsNotFound()
    {
        // DI-baked templates have no stored blob — download must fail with NotFound.
        var sut = NewSut([new FakeTemplate("di-only")], CreateContext(), new InMemoryStorage());

        var r = await sut.DownloadAsync("di-only");

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task DownloadAsync_NullOrEmptyCode_ReturnsNotFound()
    {
        var sut = NewSut([], CreateContext(), new InMemoryStorage());

        var r = await sut.DownloadAsync("   ");

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── Test infrastructure ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-templates-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed UTC instant for deterministic timestamps.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IFileStorage"/> substitute. Mirrors the behaviour the
    /// E2E fixture's InMemoryFileStorage uses but kept local to the unit-test assembly
    /// so the Infrastructure tests do not take a dependency on the E2E project.
    /// </summary>
    private sealed class InMemoryStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public bool Contains(string bucket, string key) => _objects.ContainsKey(Key(bucket, key));

        private static string Key(string bucket, string key) => $"{bucket}::{key}";

        public async Task<Result<StoredObject>> PutAsync(string bucket, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var bytes = ms.ToArray();
            var objectKey = $"unit/{Guid.NewGuid():N}";
            _objects[Key(bucket, objectKey)] = bytes;
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Result<StoredObject>.Success(new StoredObject(objectKey, sha, bytes.LongLength));
        }

        public Task<Result<Stream>> GetAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
        {
            if (_objects.TryGetValue(Key(bucket, objectKey), out var bytes))
            {
                return Task.FromResult(Result<Stream>.Success((Stream)new MemoryStream(bytes)));
            }
            return Task.FromResult(Result<Stream>.Failure(ErrorCodes.FileUnavailable, "Not found in test storage."));
        }

        public Task<Result<Uri>> PresignDownloadAsync(string bucket, string objectKey, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<Uri>.Success(new Uri($"inmemory://{bucket}/{objectKey}")));

        public Task<Result> DeleteAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
        {
            _objects.Remove(Key(bucket, objectKey));
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>
    /// Builds a tiny byte array that starts with the ZIP/DOCX magic bytes followed by a
    /// short payload. Sufficient for the magic-byte sniff + integrity assertions; not a
    /// well-formed Word document, but the service doesn't parse the content.
    /// </summary>
    private static byte[] BuildDocxBytes(string payload)
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var bytes = new byte[4 + payloadBytes.Length];
        bytes[0] = 0x50; bytes[1] = 0x4B; bytes[2] = 0x03; bytes[3] = 0x04;
        Array.Copy(payloadBytes, 0, bytes, 4, payloadBytes.Length);
        return bytes;
    }

    /// <summary>Hex-encoded SHA-256 of the input bytes; matches the service's digest discipline.</summary>
    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
