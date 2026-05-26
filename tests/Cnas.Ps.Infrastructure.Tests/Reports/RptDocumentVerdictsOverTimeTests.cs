using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6j report <c>RPT-DOCUMENT-VERDICTS-OVER-TIME</c> — daily
/// count of <see cref="Document"/> rows whose <see cref="Document.VerdictAtUtc"/> falls in the
/// half-open UTC window <c>[fromUtc, toUtc)</c>. Soft-deleted documents are excluded; days with
/// zero verdicts are emitted with a zero count so consumers can chart the series without
/// gap-filling.
/// </summary>
public class RptDocumentVerdictsOverTimeTests
{
    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOCUMENT-VERDICTS-OVER-TIME";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Day (UTC),Verdict Count");
    }

    /// <summary>
    /// Seeds two verdicts on May 1, one on May 3, plus one document with no verdict (excluded),
    /// one verdict outside the window (excluded), and one soft-deleted verdict inside the
    /// window (excluded). Verifies the dense daily series and that May 2 emits a zero row.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_EmitsDenseDailySeriesWithGapFill()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(verdict: 1, verdictAtUtc: new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        await harness.SeedDocumentAsync(verdict: 2, verdictAtUtc: new DateTime(2026, 5, 1, 14, 30, 0, DateTimeKind.Utc));
        await harness.SeedDocumentAsync(verdict: 1, verdictAtUtc: new DateTime(2026, 5, 3, 11, 0, 0, DateTimeKind.Utc));
        // No verdict — excluded.
        await harness.SeedDocumentAsync(verdict: null, verdictAtUtc: null);
        // Verdict outside the window — excluded.
        await harness.SeedDocumentAsync(verdict: 1, verdictAtUtc: new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc));
        // Soft-deleted in-window — excluded.
        await harness.SeedDocumentAsync(verdict: 1, verdictAtUtc: new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc), isActive: false);

        var paramsJson = BuildParams(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 days (May 1, May 2, May 3) — May 2 is a gap-filled zero row.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("2026-05-01,2");
        lines[2].Should().Be("2026-05-02,0");
        lines[3].Should().Be("2026-05-03,1");
    }

    /// <summary>
    /// An empty window (zero seeded documents) still emits a dense day series — every day
    /// emits a zero row so the consumer can chart without gap-filling.
    /// </summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsDenseZeroSeries()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 zero day rows.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("2026-05-01,0");
        lines[2].Should().Be("2026-05-02,0");
        lines[3].Should().Be("2026-05-03,0");
    }

    /// <summary>
    /// Edge case — an inverted window (<c>fromUtc &gt;= toUtc</c>) emits the canonical header
    /// but no day rows. Matches the empty-window fast-path in
    /// <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c> / <c>RPT-LOGIN-EVENTS-PER-DAY</c>.
    /// </summary>
    [Fact]
    public async Task Execute_InvertedWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(
            new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>Missing window parameters must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Fixed UTC moment used by the harness clock — date-arithmetic determinism.</summary>
    private static readonly DateTime HarnessClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds the parameters JSON for the [fromUtc, toUtc) half-open window.</summary>
    private static string BuildParams(DateTime fromUtc, DateTime toUtc)
        => $"{{ \"fromUtc\": \"{fromUtc.ToString("O", CultureInfo.InvariantCulture)}\", " +
           $"\"toUtc\": \"{toUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

    /// <summary>Reads the full text of a stream using UTF-8 with BOM detection.</summary>
    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Deterministic stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test harness composing EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>The in-memory database context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-doc-vot-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(HarnessClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>
        /// Seeds a single <see cref="Document"/> with the supplied verdict and verdict timestamp.
        /// A null verdict / verdict timestamp produces a row that the report must exclude.
        /// </summary>
        public async Task SeedDocumentAsync(int? verdict, DateTime? verdictAtUtc, bool isActive = true)
        {
            Db.Documents.Add(new Document
            {
                CreatedAtUtc = verdictAtUtc ?? HarnessClockNow.AddDays(-10),
                DossierId = null,
                Kind = DocumentKind.Attachment,
                Title = $"Doc-{Guid.NewGuid():N}"[..12],
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"obj-{Guid.NewGuid():N}",
                StorageBucket = "test",
                ContentSha256Hex = new string('a', 64),
                IsSigned = false,
                Verdict = verdict,
                VerdictAtUtc = verdictAtUtc,
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
