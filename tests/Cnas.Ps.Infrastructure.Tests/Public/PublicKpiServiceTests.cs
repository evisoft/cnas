using System;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.PublicServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Public;

/// <summary>
/// R0500 / TOR CF 01.02 — tests for <see cref="PublicKpiService"/>. Pins
/// four invariants:
/// <list type="bullet">
///   <item>First call materialises the snapshot from the DB.</item>
///   <item>Second call inside the 5-minute window returns the cached
///         instance and increments the cache-hit counter.</item>
///   <item>Snapshot counts faithfully reflect seeded rows (contributors,
///         insured persons, pending applications, decisions issued in
///         the last 30 days, last successful Treasury import).</item>
///   <item>An empty DB returns all-zero counts and a null Treasury
///         timestamp.</item>
/// </list>
/// </summary>
public sealed class PublicKpiServiceTests
{
    /// <summary>Deterministic UTC clock for the test suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// R0500 — First call populates the cache and returns a snapshot whose
    /// <see cref="PublicKpiSnapshotDto.ComputedAtUtc"/> equals the clock.
    /// </summary>
    [Fact]
    public async Task GetCurrentAsync_FirstCall_ComputesSnapshot()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GetCurrentAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.ComputedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>
    /// R0500 — Within the 5-minute cache window the service returns the
    /// exact same snapshot instance and increments
    /// <see cref="CnasMeter.PublicKpiSnapshotCacheHit"/>.
    /// </summary>
    [Fact]
    public async Task GetCurrentAsync_SecondCallWithinWindow_ReturnsCachedSnapshot()
    {
        var harness = Harness.Create();
        using var listener = new CounterListener("cnas.public_kpi.snapshot.cache_hit");

        var first = await harness.Service.GetCurrentAsync();
        var second = await harness.Service.GetCurrentAsync();

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // Same instance ⇒ cache hit, no recompute.
        second.Value.Should().BeSameAs(first.Value);
        listener.Total.Should().BeGreaterThanOrEqualTo(1L,
            "the second call must be served from the in-process cache");
    }

    /// <summary>
    /// R0500 — Counts in the snapshot match the seeded rows: 2 active
    /// contributors, 1 active insured person, 2 pending applications,
    /// 1 decision issued in the last 30 days, and the most recent
    /// completed Treasury import timestamp.
    /// </summary>
    [Fact]
    public async Task GetCurrentAsync_PopulatedDb_AggregatesMatchSeed()
    {
        var harness = Harness.Create();
        // 2 active contributors + 1 inactive (excluded) + 1 deactivated (excluded).
        await harness.SeedContributorAsync("01", active: true, deactivated: false);
        await harness.SeedContributorAsync("02", active: true, deactivated: false);
        await harness.SeedContributorAsync("03", active: false, deactivated: false);
        await harness.SeedContributorAsync("04", active: true, deactivated: true);

        // 1 active insured person + 1 inactive (excluded).
        await harness.SeedInsuredPersonAsync("11", active: true);
        await harness.SeedInsuredPersonAsync("12", active: false);

        // 2 pending applications (Submitted + UnderExamination) + 1 closed (excluded by pending).
        await harness.SeedApplicationAsync(ApplicationStatus.Submitted, ClockNow.AddDays(-5));
        await harness.SeedApplicationAsync(ApplicationStatus.UnderExamination, ClockNow.AddDays(-3));
        await harness.SeedApplicationAsync(ApplicationStatus.Closed, ClockNow.AddDays(-10));

        // 1 decision issued in last 30 days (Closed, UpdatedAtUtc 10 days ago).
        // The closed seed above also has UpdatedAtUtc 10 days ago — that single
        // row counts as one decision.

        // 1 completed Treasury import 2 hours ago + 1 failed (excluded).
        await harness.SeedTreasuryImportAsync(
            status: TreasuryFeedImportStatus.Completed, completedAt: ClockNow.AddHours(-2));
        await harness.SeedTreasuryImportAsync(
            status: TreasuryFeedImportStatus.Failed, completedAt: ClockNow.AddMinutes(-30));

        var result = await harness.Service.GetCurrentAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalActiveContributors.Should().Be(2);
        result.Value.TotalActiveInsuredPersons.Should().Be(1);
        result.Value.TotalPendingApplications.Should().Be(2);
        result.Value.DecisionsIssuedLast30Days.Should().Be(1);
        result.Value.LastSuccessfulTreasuryImportAtUtc.Should().Be(ClockNow.AddHours(-2));
    }

    /// <summary>
    /// R0500 — An empty DB returns all-zero counts and a null Treasury
    /// timestamp. Validates the safe default the public surface returns
    /// during cold-start before any data has been ingested.
    /// </summary>
    [Fact]
    public async Task GetCurrentAsync_EmptyDb_ReturnsZeros()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GetCurrentAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalActiveContributors.Should().Be(0);
        result.Value.TotalActiveInsuredPersons.Should().Be(0);
        result.Value.TotalPendingApplications.Should().Be(0);
        result.Value.DecisionsIssuedLast30Days.Should().Be(0);
        result.Value.LastSuccessfulTreasuryImportAtUtc.Should().BeNull();
    }

    // ─────────────────────────── Test harness ───────────────────────────

    /// <summary>Deterministic clock; one instant for every test.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and seed helpers.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required PublicKpiService Service { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            IReadOnlyCnasDbContext readDb = db;
            // Use the test-only internal constructor so the singleton scope
            // factory shim returns this exact in-memory context.
            var service = new PublicKpiService(new StubClock(ClockNow), readDb);
            return new Harness { Db = db, Service = service };
        }

        public async Task SeedContributorAsync(string idnoSuffix, bool active, bool deactivated)
        {
            Db.Contributors.Add(new Contributor
            {
                Idno = $"200000000{idnoSuffix.PadLeft(4, '0')}",
                IdnoHash = $"hash-{idnoSuffix}",
                Denumire = $"C{idnoSuffix}",
                IsActive = active,
                IsDeactivated = deactivated,
                RegisteredAtUtc = ClockNow.AddYears(-1),
                CreatedAtUtc = ClockNow.AddYears(-1),
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedInsuredPersonAsync(string idnpSuffix, bool active)
        {
            Db.InsuredPersons.Add(new InsuredPerson
            {
                Idnp = $"200000000{idnpSuffix.PadLeft(4, '0')}",
                IdnpHash = $"hashp-{idnpSuffix}",
                LastName = $"P{idnpSuffix}",
                FirstName = "Test",
                BirthDate = new DateOnly(1970, 1, 1),
                RegisteredAtUtc = ClockNow.AddYears(-1),
                IsActive = active,
                CreatedAtUtc = ClockNow.AddYears(-1),
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedApplicationAsync(ApplicationStatus status, DateTime updatedAtUtc)
        {
            Db.Applications.Add(new ServiceApplication
            {
                SolicitantId = 0,
                ServicePassportId = 0,
                Status = status,
                ReferenceNumber = $"REF-{Guid.NewGuid():N}".Substring(0, 16),
                CreatedAtUtc = ClockNow.AddDays(-20),
                UpdatedAtUtc = updatedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedTreasuryImportAsync(TreasuryFeedImportStatus status, DateTime completedAt)
        {
            Db.TreasuryFeedImports.Add(new TreasuryFeedImport
            {
                FeedDate = DateOnly.FromDateTime(completedAt),
                Status = status,
                SourceKind = TreasuryFeedSourceKind.Sftp,
                TriggerKind = TreasuryFeedTriggerKind.Scheduled,
                StartedAt = completedAt.AddMinutes(-5),
                CompletedAt = completedAt,
                CreatedAtUtc = completedAt,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-public-kpi-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Minimal <see cref="MeterListener"/> wrapper that totals a single
    /// counter's measurements. Used by the cache-hit assertion to prove
    /// the second GetCurrentAsync call did not recompute.
    /// </summary>
    private sealed class CounterListener : IDisposable
    {
        private readonly MeterListener _listener;
        private long _total;

        public long Total => System.Threading.Interlocked.Read(ref _total);

        public CounterListener(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName &&
                        instrument.Name == instrumentName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
                System.Threading.Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
