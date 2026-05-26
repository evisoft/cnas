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
/// Integration tests for the Annex 6h report <c>RPT-LOGIN-EVENTS-PER-DAY</c> — daily count
/// of audit-log rows whose <see cref="AuditLog.EventCode"/> begins with <c>USER.LOGIN.</c>,
/// split into success / failure columns. The success bucket counts codes ending in
/// <c>.SUCCESS</c>; every other USER.LOGIN.* code lands in the failure bucket. The output
/// is a dense daily series with gap-filled zero rows.
/// </summary>
public class RptLoginEventsPerDayTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-LOGIN-EVENTS-PER-DAY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-3), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Day (UTC),Success Count,Failure Count");
    }

    /// <summary>
    /// Seeds two SUCCESS and one FAILURE on day-2, one LOCKED on day-4, and one out-of-window
    /// SUCCESS. Verifies per-day success / failure split, that LOCKED counts as a failure
    /// (anything not ending in <c>.SUCCESS</c>), and dense rows for the empty days.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_SplitsSuccessAndFailurePerDay()
    {
        var harness = Harness.Create();
        var d0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await harness.SeedLoginAsync("USER.LOGIN.SUCCESS", d0.AddDays(1).AddHours(8));
        await harness.SeedLoginAsync("USER.LOGIN.SUCCESS", d0.AddDays(1).AddHours(9));
        await harness.SeedLoginAsync("USER.LOGIN.FAILURE", d0.AddDays(1).AddHours(10));
        await harness.SeedLoginAsync("USER.LOGIN.LOCKED",  d0.AddDays(3).AddHours(8));
        // Non-login audit row in window — excluded because it does not match USER.LOGIN.%.
        await harness.SeedLoginAsync("DOSSIER.CREATED", d0.AddDays(2).AddHours(8));
        // Out-of-window — excluded.
        await harness.SeedLoginAsync("USER.LOGIN.SUCCESS", d0.AddDays(-3));

        // Half-open window [2026-05-01, 2026-05-06) → 5 days.
        var paramsJson = BuildParams(d0, d0.AddDays(5));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 5 daily rows (dense).
        lines.Should().HaveCount(6);
        lines.Should().Contain("2026-05-01,0,0");
        lines.Should().Contain("2026-05-02,2,1");
        lines.Should().Contain("2026-05-03,0,0");
        lines.Should().Contain("2026-05-04,0,1"); // LOCKED counts as failure
        lines.Should().Contain("2026-05-05,0,0");
    }

    /// <summary>An empty window still emits one zero row per day in range.</summary>
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
        lines.Should().Contain("2026-05-10,0,0");
        lines.Should().Contain("2026-05-11,0,0");
        lines.Should().Contain("2026-05-12,0,0");
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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-login-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="AuditLog"/> row with the supplied event code and timestamp.</summary>
        public async Task SeedLoginAsync(string eventCode, DateTime eventAtUtc)
        {
            Db.AuditLogs.Add(new AuditLog
            {
                CreatedAtUtc = eventAtUtc,
                EventAtUtc = eventAtUtc,
                EventCode = eventCode,
                Severity = AuditSeverity.Information,
                ActorId = "tester",
                IsActive = true,
                // R0194 — placeholder chain values; not exercised by these tests.
                PrevHash = "GENESIS",
                RowHash = string.Empty,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
