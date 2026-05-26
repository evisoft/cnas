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
/// Integration tests for the Annex 6j report <c>RPT-AUDIT-EVENTS-BY-ACTOR</c> — count of
/// <see cref="AuditLog"/> rows by <see cref="AuditLog.ActorId"/> inside the half-open UTC
/// window <c>[fromUtc, toUtc)</c>. Soft-deleted rows are excluded; rows are ordered by Count
/// desc, then Actor (Ordinal); an optional <c>topN</c> parameter truncates the result.
/// </summary>
public class RptAuditEventsByActorTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-AUDIT-EVENTS-BY-ACTOR";

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
        firstLine.Should().Be("Actor,Count");
    }

    /// <summary>
    /// Seeds three events for actor "alice", two for "bob", and one for "system" — all in
    /// window — plus one out-of-window for alice (excluded) and one soft-deleted for bob
    /// (excluded). Verifies Count desc ordering with the <c>"system"</c> sentinel retained
    /// (unlike RPT-ACTIVE-USERS-LAST-30D which filters it out).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByActorAndRetainsSystemSentinel()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync("alice", ClockNow.AddDays(-1));
        await harness.SeedAuditLogAsync("alice", ClockNow.AddDays(-2));
        await harness.SeedAuditLogAsync("alice", ClockNow.AddDays(-3));
        await harness.SeedAuditLogAsync("bob", ClockNow.AddDays(-2));
        await harness.SeedAuditLogAsync("bob", ClockNow.AddDays(-3));
        await harness.SeedAuditLogAsync("system", ClockNow.AddDays(-4));
        // Out-of-window — excluded.
        await harness.SeedAuditLogAsync("alice", ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedAuditLogAsync("bob", ClockNow.AddDays(-5), isActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 actor rows; "system" is retained on purpose.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("alice,3");
        lines[2].Should().Be("bob,2");
        lines[3].Should().Be("system,1");
    }

    /// <summary>An empty window emits only the header row.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>
    /// Edge case — the <c>topN</c> parameter truncates the result list to the supplied limit
    /// while preserving Count desc ordering.
    /// </summary>
    [Fact]
    public async Task Execute_WithTopN_TruncatesResultList()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync("alpha", ClockNow.AddDays(-1));
        await harness.SeedAuditLogAsync("alpha", ClockNow.AddDays(-2));
        await harness.SeedAuditLogAsync("alpha", ClockNow.AddDays(-3));
        await harness.SeedAuditLogAsync("beta", ClockNow.AddDays(-1));
        await harness.SeedAuditLogAsync("beta", ClockNow.AddDays(-2));
        await harness.SeedAuditLogAsync("gamma", ClockNow.AddDays(-1));

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture)}\", " +
                         $"\"toUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\", " +
                         $"\"topN\": 2 }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + topN(2) rows; gamma dropped.
        lines.Should().HaveCount(3);
        lines[1].Should().Be("alpha,3");
        lines[2].Should().Be("beta,2");
        text.Should().NotContain("gamma");
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
                .UseInMemoryDatabase($"cnas-rpt-aud-actor-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="AuditLog"/> row with the supplied actor id and event timestamp.</summary>
        public async Task SeedAuditLogAsync(string actorId, DateTime eventAtUtc, bool isActive = true)
        {
            Db.AuditLogs.Add(new AuditLog
            {
                CreatedAtUtc = eventAtUtc,
                EventAtUtc = eventAtUtc,
                EventCode = "EVT.X",
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
