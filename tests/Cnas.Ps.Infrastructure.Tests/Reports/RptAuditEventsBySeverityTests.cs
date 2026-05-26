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
/// Integration tests for the Annex 6h report <c>RPT-AUDIT-EVENTS-BY-SEVERITY</c> —
/// distribution of <see cref="AuditLog"/> rows by <see cref="AuditSeverity"/> inside the UTC
/// window <c>[fromUtc, toUtc)</c>. All four severity values are emitted densely (Information,
/// Notice, Sensitive, Critical) even when a bucket has zero traffic; soft-deleted rows are
/// excluded; the window predicate uses <see cref="AuditLog.EventAtUtc"/> rather than
/// <see cref="AuditableEntity.CreatedAtUtc"/>.
/// </summary>
public class RptAuditEventsBySeverityTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-AUDIT-EVENTS-BY-SEVERITY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Severity,Count");
    }

    /// <summary>
    /// Seeds two Information, one Notice, three Sensitive and one Critical event in window plus
    /// one out-of-window and one soft-deleted in-window — both excluded. Verifies the per-bucket
    /// counts and the dense-row contract (Notice bucket gets a row even if empty here).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsBySeverityWithDenseBuckets()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync(AuditSeverity.Information, ClockNow.AddDays(-1));
        await harness.SeedAuditLogAsync(AuditSeverity.Information, ClockNow.AddDays(-2));
        await harness.SeedAuditLogAsync(AuditSeverity.Notice,      ClockNow.AddDays(-3));
        await harness.SeedAuditLogAsync(AuditSeverity.Sensitive,   ClockNow.AddDays(-4));
        await harness.SeedAuditLogAsync(AuditSeverity.Sensitive,   ClockNow.AddDays(-5));
        await harness.SeedAuditLogAsync(AuditSeverity.Sensitive,   ClockNow.AddDays(-6));
        await harness.SeedAuditLogAsync(AuditSeverity.Critical,    ClockNow.AddDays(-7));
        // Out-of-window — excluded.
        await harness.SeedAuditLogAsync(AuditSeverity.Critical, ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedAuditLogAsync(AuditSeverity.Critical, ClockNow.AddDays(-3), isActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 4 dense severity rows.
        lines.Should().HaveCount(5);
        lines.Should().Contain("Information,2");
        lines.Should().Contain("Notice,1");
        lines.Should().Contain("Sensitive,3");
        lines.Should().Contain("Critical,1");
    }

    /// <summary>Every severity bucket is emitted densely even when the window is empty.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsAllFourSeverityRowsWithZero()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(5);
        lines.Should().Contain("Information,0");
        lines.Should().Contain("Notice,0");
        lines.Should().Contain("Sensitive,0");
        lines.Should().Contain("Critical,0");
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

        /// <summary>Monotonic event-code counter so synthetic rows do not collide.</summary>
        private long _eventCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-aud-sev-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="AuditLog"/> with the supplied severity and event timestamp.</summary>
        public async Task SeedAuditLogAsync(AuditSeverity severity, DateTime eventAtUtc, bool isActive = true)
        {
            _eventCounter++;
            Db.AuditLogs.Add(new AuditLog
            {
                CreatedAtUtc = eventAtUtc,
                EventAtUtc = eventAtUtc,
                EventCode = $"EVT_{_eventCounter:D4}",
                Severity = severity,
                ActorId = "tester",
                IsActive = isActive,
                // R0194 — these reports query AuditLog rows but the chain
                // integrity is out of scope here; placeholder hashes keep the
                // schema happy without participating in the verifier path.
                PrevHash = "GENESIS",
                RowHash = string.Empty,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
