using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6h report <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c> — daily count
/// of <see cref="Document"/> rows uploaded in the UTC window <c>[fromUtc, toUtc)</c>. The
/// output is a dense daily series — days with zero uploads still appear with a zero count.
/// Soft-deleted documents are excluded.
/// </summary>
public class RptDocumentUploadVolumesTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOCUMENT-UPLOAD-VOLUMES";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(WindowStart(), WindowEnd());
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Day (UTC),Upload Count");
    }

    /// <summary>
    /// Seeds three uploads — two on day-2 of the window, one on day-4 — plus one out-of-window
    /// and one soft-deleted. Verifies the per-day counts and the dense-row contract (day-3 must
    /// appear with a 0).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByDayAndGapFills()
    {
        var harness = Harness.Create();
        var d0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc); // window start
        // 2 uploads on 2026-05-02; 1 on 2026-05-04; nothing on 2026-05-03 and 2026-05-05.
        await harness.SeedDocumentAsync(d0.AddDays(1).AddHours(8));
        await harness.SeedDocumentAsync(d0.AddDays(1).AddHours(15));
        await harness.SeedDocumentAsync(d0.AddDays(3).AddHours(10));
        // Out-of-window — excluded.
        await harness.SeedDocumentAsync(d0.AddDays(-3));
        // Soft-deleted in-window — excluded.
        await harness.SeedDocumentAsync(d0.AddDays(2).AddHours(9), isActive: false);

        // Half-open window [2026-05-01, 2026-05-06) → 5 days.
        var paramsJson = BuildParams(d0, d0.AddDays(5));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 5 daily rows.
        lines.Should().HaveCount(6);
        lines.Should().Contain("2026-05-01,0");
        lines.Should().Contain("2026-05-02,2");
        lines.Should().Contain("2026-05-03,0");
        lines.Should().Contain("2026-05-04,1");
        lines.Should().Contain("2026-05-05,0");
    }

    /// <summary>An empty window still emits one row per day in range with a zero count.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsDenseZeroSeries()
    {
        var harness = Harness.Create();

        var d0 = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(d0, d0.AddDays(3));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        lines.Should().Contain("2026-05-10,0");
        lines.Should().Contain("2026-05-11,0");
        lines.Should().Contain("2026-05-12,0");
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

    /// <summary>Default window start (30 days back from <see cref="ClockNow"/>).</summary>
    private static DateTime WindowStart() => ClockNow.AddDays(-30);

    /// <summary>Default window end (<see cref="ClockNow"/>).</summary>
    private static DateTime WindowEnd() => ClockNow;

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
                .UseInMemoryDatabase($"cnas-rpt-docupload-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="Document"/> at the supplied creation timestamp.</summary>
        public async Task SeedDocumentAsync(DateTime createdUtc, bool isActive = true)
        {
            Db.Documents.Add(new Document
            {
                CreatedAtUtc = createdUtc,
                DossierId = null,
                Kind = DocumentKind.Attachment,
                Title = $"Doc-{Guid.NewGuid():N}"[..12],
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"obj-{Guid.NewGuid():N}",
                StorageBucket = "test",
                ContentSha256Hex = new string('a', 64),
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
