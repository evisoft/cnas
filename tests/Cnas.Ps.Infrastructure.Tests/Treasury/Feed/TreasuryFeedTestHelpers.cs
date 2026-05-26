using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — shared helpers for the Treasury feed test suite.
/// </summary>
internal static class TreasuryFeedTestHelpers
{
    /// <summary>Canonical "now" used across the feed tests (2026-05-23 04:00 UTC).</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical "feed date" — yesterday relative to <see cref="ClockNow"/>.</summary>
    public static readonly DateOnly FeedDate = new(2026, 5, 22);

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Builds a fresh EF Core InMemory context backed by a unique store.</summary>
    /// <returns>A new context.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-treasury-feed-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    /// <returns>Configured mock.</returns>
    public static ISqidService NewSqidMock()
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

    /// <summary>Audit mock that captures every event code written.</summary>
    /// <param name="codes">Out parameter — captured codes list.</param>
    /// <returns>Configured mock.</returns>
    public static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    /// <summary>Caller-context mock returning sqid USR-1.</summary>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-feed");
        return c;
    }

    /// <summary>
    /// Deterministic hasher that returns <c>"HASH:" + canonicalised input</c>.
    /// Lets tests seed contributors whose IdnoHash is computed the same way.
    /// </summary>
    public sealed class DeterministicHasherStub : IDeterministicHasher
    {
        /// <inheritdoc />
        public string ComputeHash(string canonicalValue)
        {
            ArgumentNullException.ThrowIfNull(canonicalValue);
            return "HASH:" + canonicalValue.Trim().ToUpperInvariant();
        }
    }

    /// <summary>Builds the importer with sensible defaults plus an overridable source.</summary>
    /// <param name="db">Writer context.</param>
    /// <param name="source">Feed source.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Importer instance.</returns>
    public static TreasuryFeedImporter NewImporter(
        CnasDbContext db,
        ITreasuryFeedSource source,
        IAuditService audit)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            source: source,
            parser: new TreasuryFeedParser(),
            idHasher: new DeterministicHasherStub(),
            logger: NullLogger<TreasuryFeedImporter>.Instance);

    /// <summary>Builds the admin service with sensible defaults.</summary>
    /// <param name="db">Writer / reader context (shared in InMemory tests).</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="importer">Importer dependency.</param>
    /// <returns>Admin service instance.</returns>
    public static TreasuryFeedAdminService NewAdminService(
        CnasDbContext db,
        IAuditService audit,
        ITreasuryFeedImporter importer)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            importer: importer,
            filterValidator: new TreasuryFeedImportFilterValidator(),
            rowFilterValidator: new TreasuryFeedImportRowFilterValidator());

    /// <summary>
    /// Seeds an active Contributor with the supplied IDNO; the
    /// IdnoHash column uses the stub hasher format ("HASH:" + canonical).
    /// </summary>
    /// <param name="db">Context.</param>
    /// <param name="idno">13-digit IDNO.</param>
    /// <returns>The persisted contributor's id.</returns>
    public static async Task<long> SeedContributorAsync(CnasDbContext db, string idno)
    {
        var c = new Contributor
        {
            Idno = idno,
            IdnoHash = "HASH:" + idno.Trim().ToUpperInvariant(),
            Denumire = "Test Payer SRL",
            IsInsolvent = false,
            RegisteredAtUtc = ClockNow,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>
    /// Builds a small Treasury-feed CSV with the supplied rows. The header
    /// row is always identical; each data row is comma-joined verbatim.
    /// </summary>
    /// <param name="rows">Tuples (ReceiptNumber, ReceiptDate, PayerIdno, PayerName, AmountMdl, TreasuryCode, Reference).</param>
    /// <returns>UTF-8 bytes of the resulting CSV.</returns>
    public static byte[] BuildCsv(params (string ReceiptNumber, string ReceiptDate, string PayerIdno, string PayerName, string AmountMdl, string TreasuryCode, string Reference)[] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ReceiptNumber,ReceiptDate,PayerIdno,PayerName,AmountMdl,TreasuryCode,Reference");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", r.ReceiptNumber, r.ReceiptDate, r.PayerIdno, r.PayerName, r.AmountMdl, r.TreasuryCode, r.Reference));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Computes the SHA-256 hex digest of the supplied bytes.</summary>
    /// <param name="bytes">Byte array.</param>
    /// <returns>64-character lower-case hex string.</returns>
    public static string Sha256Hex(byte[] bytes)
    {
        var d = SHA256.HashData(bytes);
        var sb = new StringBuilder(d.Length * 2);
        for (int i = 0; i < d.Length; i++)
        {
            sb.Append(d[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
