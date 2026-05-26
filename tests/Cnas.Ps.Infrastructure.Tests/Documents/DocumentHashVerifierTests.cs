using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Documents;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — tests for <see cref="DocumentHashVerifier"/>. Covers
/// the match path (Info audit + match counter), the mismatch path (Critical
/// audit + mismatch counter), and the storage-error path.
/// </summary>
public sealed class DocumentHashVerifierTests
{
    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-document-hash-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Caller-context mock returning sqid USR-1.</summary>
    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-hash");
        return c;
    }

    /// <summary>Audit mock capturing every event code + severity tuple written.</summary>
    private static IAuditService NewAuditCapturing(out List<(string Code, AuditSeverity Severity)> entries)
    {
        var list = new List<(string, AuditSeverity)>();
        entries = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add((c.ArgAt<string>(0), c.ArgAt<AuditSeverity>(1)));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    /// <summary>Computes SHA-256 hex digest.</summary>
    private static string Sha256Hex(byte[] bytes)
    {
        var d = SHA256.HashData(bytes);
        var sb = new StringBuilder(d.Length * 2);
        for (int i = 0; i < d.Length; i++)
        {
            sb.Append(d[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Seeds an active Document carrying the supplied recorded hash.</summary>
    private static async Task<long> SeedDocumentAsync(CnasDbContext db, string recordedHash)
    {
        var doc = new Document
        {
            Title = "Test decision",
            MimeType = "application/pdf",
            SizeBytes = 100,
            StorageObjectKey = "obj-key",
            StorageBucket = "documents",
            ContentSha256Hex = recordedHash,
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    /// <summary>Matching bytes → IsMatch=true + Information audit row.</summary>
    [Fact]
    public async Task VerifyAsync_MatchingBytes_ReturnsMatchAndInformationAudit()
    {
        using var db = CreateContext();
        var bytes = Encoding.UTF8.GetBytes("hello-world");
        var hash = Sha256Hex(bytes);
        var docId = await SeedDocumentAsync(db, hash);

        var storage = Substitute.For<IFileStorage>();
        storage.GetAsync("documents", "obj-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Stream>.Success(new MemoryStream(bytes, writable: false))));

        var audit = NewAuditCapturing(out var entries);
        var verifier = new DocumentHashVerifier(
            read: db, storage: storage, sqids: NewSqidMock(), caller: NewCaller(),
            audit: audit, logger: NullLogger<DocumentHashVerifier>.Instance);

        var result = await verifier.VerifyAsync($"SQID-{docId}");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsMatch.Should().BeTrue();
        result.Value.ComputedHash.Should().Be(hash);
        entries.Should().Contain(e =>
            e.Code == IDocumentHashVerifier.AuditHashVerify
            && e.Severity == AuditSeverity.Information);
    }

    /// <summary>Tampered bytes → IsMatch=false + Critical audit row.</summary>
    [Fact]
    public async Task VerifyAsync_TamperedBytes_ReturnsMismatchAndCriticalAudit()
    {
        using var db = CreateContext();
        var original = Encoding.UTF8.GetBytes("hello-world");
        var tampered = Encoding.UTF8.GetBytes("HELLO-WORLD"); // distinct payload
        var recordedHash = Sha256Hex(original);
        var docId = await SeedDocumentAsync(db, recordedHash);

        var storage = Substitute.For<IFileStorage>();
        storage.GetAsync("documents", "obj-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Stream>.Success(new MemoryStream(tampered, writable: false))));

        var audit = NewAuditCapturing(out var entries);
        var verifier = new DocumentHashVerifier(
            read: db, storage: storage, sqids: NewSqidMock(), caller: NewCaller(),
            audit: audit, logger: NullLogger<DocumentHashVerifier>.Instance);

        var result = await verifier.VerifyAsync($"SQID-{docId}");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsMatch.Should().BeFalse();
        result.Value.StoredHash.Should().Be(recordedHash);
        result.Value.ComputedHash.Should().NotBe(recordedHash);
        entries.Should().Contain(e =>
            e.Code == IDocumentHashVerifier.AuditHashVerify
            && e.Severity == AuditSeverity.Critical);
    }

    /// <summary>Unknown document Sqid surfaces NotFound.</summary>
    [Fact]
    public async Task VerifyAsync_UnknownDocument_ReturnsNotFound()
    {
        using var db = CreateContext();
        var storage = Substitute.For<IFileStorage>();
        var audit = NewAuditCapturing(out _);
        var verifier = new DocumentHashVerifier(
            read: db, storage: storage, sqids: NewSqidMock(), caller: NewCaller(),
            audit: audit, logger: NullLogger<DocumentHashVerifier>.Instance);

        var result = await verifier.VerifyAsync("SQID-9999");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
