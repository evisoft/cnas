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
/// Integration tests for the Annex 6h report <c>RPT-ACTIVE-USERS-LAST-30D</c> — count of
/// distinct non-"system" <see cref="AuditLog.ActorId"/> values inside the trailing window
/// ending at the supplied <c>asOfUtc</c>. The window defaults to 30 days; callers may
/// override with the <c>windowDays</c> parameter, clamped to <c>[1, 366]</c>.
/// </summary>
public class RptActiveUsersLast30DaysTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-ACTIVE-USERS-LAST-30D";

    /// <summary>Locks the report's column shape (one summary row — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Window From (UTC),Window To (UTC),Active User Count");
    }

    /// <summary>
    /// Seeds three distinct user actors with at least one in-window event each, plus one
    /// system-generated event (excluded), plus an out-of-window actor (excluded), plus a
    /// duplicate event for one of the in-window actors (still distinct-counted once).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_CountsDistinctNonSystemActors()
    {
        var harness = Harness.Create();
        await harness.SeedAuditAsync("user-A", ClockNow.AddDays(-1));
        await harness.SeedAuditAsync("user-A", ClockNow.AddDays(-2)); // duplicate — still 1
        await harness.SeedAuditAsync("user-B", ClockNow.AddDays(-3));
        await harness.SeedAuditAsync("user-C", ClockNow.AddDays(-15));
        // System actor — excluded.
        await harness.SeedAuditAsync("system", ClockNow.AddDays(-2));
        // Out-of-window actor — excluded.
        await harness.SeedAuditAsync("user-D", ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedAuditAsync("user-E", ClockNow.AddDays(-5), isActive: false);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Header + 1 summary row.
        lines.Should().HaveCount(2);
        // The summary row's tail column is the distinct-count: 3 (A, B, C).
        lines[1].Should().EndWith(",3");
    }

    /// <summary>An empty audit log produces a single summary row with zero.</summary>
    [Fact]
    public async Task Execute_EmptyAuditLog_ReportsZero()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[1].Should().EndWith(",0");
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

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
                .UseInMemoryDatabase($"cnas-rpt-active-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="AuditLog"/> row with the supplied actor + event timestamp.</summary>
        public async Task SeedAuditAsync(string actorId, DateTime eventAtUtc, bool isActive = true)
        {
            _eventCounter++;
            Db.AuditLogs.Add(new AuditLog
            {
                CreatedAtUtc = eventAtUtc,
                EventAtUtc = eventAtUtc,
                EventCode = $"EVT_{_eventCounter:D4}",
                Severity = AuditSeverity.Information,
                ActorId = actorId,
                IsActive = isActive,
                // R0194 — placeholder chain values; not exercised by these tests.
                PrevHash = "GENESIS",
                RowHash = string.Empty,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
