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
/// Integration tests for the Annex 6g report <c>RPT-WORKFLOW-BACKLOG-AGE</c> — distribution
/// of open workflow-task ages (in days) across the five fixed buckets <c>0-1d</c>,
/// <c>1-3d</c>, <c>3-7d</c>, <c>7-14d</c>, <c>&gt;14d</c>. Source: <see cref="WorkflowTask"/>
/// rows whose <see cref="WorkflowTask.CompletedAtUtc"/> is null. Histogram is dense — all
/// five buckets emitted even when the backlog is empty.
/// </summary>
public class RptWorkflowBacklogAgeTests
{
    /// <summary>Fixed UTC clock so bucket boundaries are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-WORKFLOW-BACKLOG-AGE";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Age Bucket,Count");
    }

    /// <summary>
    /// Seeds one open task in each bucket plus a completed task (excluded). Verifies each
    /// bucket holds count = 1.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_BucketsByAge()
    {
        var harness = Harness.Create();
        await harness.SeedOpenTaskAsync(ClockNow.AddHours(-12));   // 0-1d
        await harness.SeedOpenTaskAsync(ClockNow.AddDays(-2));     // 1-3d
        await harness.SeedOpenTaskAsync(ClockNow.AddDays(-5));     // 3-7d
        await harness.SeedOpenTaskAsync(ClockNow.AddDays(-10));    // 7-14d
        await harness.SeedOpenTaskAsync(ClockNow.AddDays(-30));    // >14d
        // Completed task — must be excluded.
        await harness.SeedCompletedTaskAsync(ClockNow.AddDays(-5), ClockNow.AddDays(-1));

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("0-1d,1");
        lines.Should().Contain("1-3d,1");
        lines.Should().Contain("3-7d,1");
        lines.Should().Contain("7-14d,1");
        lines.Should().Contain(">14d,1");
    }

    /// <summary>All five buckets are always emitted, even when the backlog is empty.</summary>
    [Fact]
    public async Task Execute_DenseHistogram_EmitsAllFiveBucketsEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("0-1d,0");
        lines.Should().Contain("1-3d,0");
        lines.Should().Contain("3-7d,0");
        lines.Should().Contain("7-14d,0");
        lines.Should().Contain(">14d,0");
    }

    /// <summary>Completed tasks (CompletedAtUtc non-null) must be excluded from the backlog.</summary>
    [Fact]
    public async Task Execute_ExcludesCompletedTasks()
    {
        var harness = Harness.Create();
        await harness.SeedOpenTaskAsync(ClockNow.AddHours(-12));
        await harness.SeedCompletedTaskAsync(ClockNow.AddHours(-12), ClockNow.AddHours(-2));

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Only the open task survives.
        lines.Should().Contain("0-1d,1");
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

        /// <summary>Monotonic counter so dossier ids are unique per seeded task.</summary>
        private long _dossierIdCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-backlog-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an open (uncompleted) <see cref="WorkflowTask"/> with the supplied creation time.</summary>
        public async Task SeedOpenTaskAsync(DateTime createdUtc)
        {
            _dossierIdCounter++;
            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = createdUtc,
                DossierId = _dossierIdCounter,
                Title = "Examine",
                Status = WorkflowTaskStatus.InProgress,
                CompletedAtUtc = null,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }

        /// <summary>Seeds a completed <see cref="WorkflowTask"/> with the supplied lifecycle timestamps.</summary>
        public async Task SeedCompletedTaskAsync(DateTime createdUtc, DateTime completedUtc)
        {
            _dossierIdCounter++;
            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = createdUtc,
                DossierId = _dossierIdCounter,
                Title = "Examine",
                Status = WorkflowTaskStatus.Completed,
                CompletedAtUtc = completedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }
}
