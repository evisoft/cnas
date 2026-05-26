using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Kpi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — tests for <c>KpiSnapshotService</c>: orchestrates
/// every registered <see cref="IKpiCalculator"/>, upserts the emitted
/// entries, and exposes the dashboard read API.
/// </summary>
public sealed class KpiSnapshotServiceTests
{
    /// <summary>Stable snapshot date used across the test suite.</summary>
    private static readonly DateOnly SnapshotDate = new(2026, 5, 22);

    [Fact]
    public async Task RunForDateAsync_InvokesEveryRegisteredCalculator_AndPersistsEntries()
    {
        var harness = Harness.Create(
            new StubCalculator("KPI.Alpha", new KpiSnapshotEntry("KPI.Alpha", 1m, KpiValueUnits.Count, string.Empty, string.Empty)),
            new StubCalculator("KPI.Beta",
                new KpiSnapshotEntry("KPI.Beta", 2m, KpiValueUnits.Count, "MD-CH", string.Empty),
                new KpiSnapshotEntry("KPI.Beta", 3m, KpiValueUnits.Count, "MD-BL", string.Empty)));

        var result = await harness.Service.RunForDateAsync(SnapshotDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.CalculatorsRun.Should().Be(2);
        result.Value.RowsUpserted.Should().Be(3);
        var rows = await harness.Db.KpiSnapshots.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().Contain(r => r.KpiCode == "KPI.Alpha" && r.Value == 1m);
        rows.Should().Contain(r => r.KpiCode == "KPI.Beta" && r.Value == 2m && r.Dimension1 == "MD-CH");
        rows.Should().Contain(r => r.KpiCode == "KPI.Beta" && r.Value == 3m && r.Dimension1 == "MD-BL");
    }

    [Fact]
    public async Task RunForDateAsync_ReRunForSameDate_Upserts_LatestValueWins()
    {
        var first = new StubCalculator("KPI.Alpha",
            new KpiSnapshotEntry("KPI.Alpha", 1m, KpiValueUnits.Count, string.Empty, string.Empty));
        var harness = Harness.Create(first);

        (await harness.Service.RunForDateAsync(SnapshotDate)).IsSuccess.Should().BeTrue();
        // Replace the calculator value and re-run.
        first.SetEntries(new KpiSnapshotEntry("KPI.Alpha", 42m, KpiValueUnits.Count, string.Empty, string.Empty));
        (await harness.Service.RunForDateAsync(SnapshotDate)).IsSuccess.Should().BeTrue();

        var rows = await harness.Db.KpiSnapshots.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshot>(r => r.KpiCode == "KPI.Alpha" && r.Value == 42m);
    }

    [Fact]
    public async Task RunForDateAsync_EmitsKpiSnapshotCompletedAuditOnce()
    {
        var harness = Harness.Create(
            new StubCalculator("KPI.Alpha", new KpiSnapshotEntry("KPI.Alpha", 1m, KpiValueUnits.Count, string.Empty, string.Empty)));

        await harness.Service.RunForDateAsync(SnapshotDate);

        await harness.Audit.Received(1).RecordAsync(
            "KPI.SNAPSHOT.COMPLETED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(d => d.Contains("\"calculatorsRun\":1", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Query / GetLatest ───────────────────────

    [Fact]
    public async Task QueryAsync_ReturnsRowsOrderedBySnapshotDateDesc_AndKpiCodeAsc()
    {
        var harness = Harness.Create();
        await SeedAsync(harness.Db, new(2026, 5, 20), "Beta", 2m);
        await SeedAsync(harness.Db, new(2026, 5, 20), "Alpha", 1m);
        await SeedAsync(harness.Db, new(2026, 5, 22), "Alpha", 10m);
        await SeedAsync(harness.Db, new(2026, 5, 18), "Alpha", 100m); // outside range

        var rows = await harness.Service.QueryAsync(
            new(2026, 5, 19), new(2026, 5, 22), kpiCodeFilter: null);

        rows.Should().HaveCount(3);
        rows[0].SnapshotDate.Should().Be(new(2026, 5, 22));
        rows[0].KpiCode.Should().Be("Alpha");
        rows[1].SnapshotDate.Should().Be(new(2026, 5, 20));
        rows[1].KpiCode.Should().Be("Alpha");
        rows[2].KpiCode.Should().Be("Beta");
    }

    [Fact]
    public async Task QueryAsync_FiltersByKpiCodeWhenSupplied()
    {
        var harness = Harness.Create();
        await SeedAsync(harness.Db, new(2026, 5, 22), "Alpha", 1m);
        await SeedAsync(harness.Db, new(2026, 5, 22), "Beta", 2m);

        var rows = await harness.Service.QueryAsync(
            new(2026, 5, 22), new(2026, 5, 22), kpiCodeFilter: "Alpha");

        rows.Should().ContainSingle()
            .Which.KpiCode.Should().Be("Alpha");
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsMostRecentPerKpiCode_SummedAcrossDimensions()
    {
        var harness = Harness.Create();
        await SeedAsync(harness.Db, new(2026, 5, 20), "Alpha", 1m);
        await SeedAsync(harness.Db, new(2026, 5, 22), "Alpha", 5m, dimension1: "MD-CH");
        await SeedAsync(harness.Db, new(2026, 5, 22), "Alpha", 7m, dimension1: "MD-BL");
        await SeedAsync(harness.Db, new(2026, 5, 22), "Beta", 3m);

        var codes = new[] { "Alpha", "Beta", "Unknown" };
        var latest = await harness.Service.GetLatestAsync(codes);

        latest.Should().HaveCount(2);
        latest["Alpha"].Should().Be(12m);   // sum of two MD-CH/MD-BL rows on the latest date
        latest["Beta"].Should().Be(3m);
        latest.ContainsKey("Unknown").Should().BeFalse();
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Seeds a KPI snapshot row.</summary>
    private static async Task SeedAsync(
        CnasDbContext db, DateOnly date, string kpiCode, decimal value,
        string dimension1 = "", string dimension2 = "")
    {
        db.KpiSnapshots.Add(new KpiSnapshot
        {
            CreatedAtUtc = DateTime.UtcNow,
            SnapshotDate = date,
            KpiCode = kpiCode,
            Value = value,
            ValueUnit = KpiValueUnits.Count,
            Dimension1 = dimension1,
            Dimension2 = dimension2,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Deterministic clock helper.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test double for an <see cref="IKpiCalculator"/>.</summary>
    private sealed class StubCalculator(string kpiCode, params KpiSnapshotEntry[] entries) : IKpiCalculator
    {
        private IReadOnlyList<KpiSnapshotEntry> _entries = entries;

        public string KpiCode { get; } = kpiCode;

        public void SetEntries(params KpiSnapshotEntry[] entries) => _entries = entries;

        public Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
            DateOnly snapshotDate, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }

    /// <summary>
    /// Test fixture wiring the snapshot service against an InMemory DbContext +
    /// fake calculators + NSub'd audit service.
    /// </summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required IKpiSnapshotService Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create(params IKpiCalculator[] calculators)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-kpi-svc-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var clock = new StubClock(new DateTime(2026, 5, 22, 2, 0, 0, DateTimeKind.Utc));

            var service = new KpiSnapshotService(
                db, db, calculators, audit, sqids, clock,
                NullLogger<KpiSnapshotService>.Instance);

            return new Harness { Db = db, Service = service, Audit = audit };
        }
    }
}
