using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Classifiers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Services.Classifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Classifiers;

/// <summary>
/// R0402 / TOR CF 17.09 — integration tests for
/// <see cref="ClassifierReferenceGuard"/> + the wiring of
/// <see cref="IClassifierService.DeactivateAsync"/> into the guard.
/// </summary>
/// <remarks>
/// Uses the in-memory EF Core provider (the same pattern the rest of the
/// Infrastructure test suite uses). The reference-blocking contract is
/// pure-read so neither connection topology nor migrations are exercised.
/// </remarks>
public sealed class ClassifierReferenceGuardTests
{
    /// <summary>Deterministic UTC clock used so audit fields stay stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    // ─────────────────────────── Guard direct tests ───────────────────────────

    /// <summary>
    /// R0402 — A scheme that the guard does not yet map (here: <c>UNKNOWN_SCHEME</c>)
    /// must return zero references with an empty entity list. New schemes hit this
    /// branch until a maintainer extends the dispatch table.
    /// </summary>
    [Fact]
    public async Task ScanAsync_UnknownScheme_ReturnsZeroReferences()
    {
        var harness = Harness.Create();

        var result = await harness.Guard.ScanAsync(
            schemeCode: "UNKNOWN_SCHEME",
            value: "any-value");

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferencingRowCount.Should().Be(0);
        result.Value.ReferencingEntities.Should().BeEmpty();
        result.Value.SchemeCode.Should().Be("UNKNOWN_SCHEME");
        result.Value.Value.Should().Be("any-value");
    }

    /// <summary>
    /// R0402 — When the known <c>CAEM</c> scheme has zero citing
    /// <see cref="Contributor"/> rows, the per-entity count is zero and the
    /// total is zero. The entity entry is still present (visible "we looked
    /// here and found nothing").
    /// </summary>
    [Fact]
    public async Task ScanAsync_KnownSchemeWithNoReferences_ReturnsZero()
    {
        var harness = Harness.Create();
        // Seed an unrelated contributor with a different CAEM code.
        await harness.SeedContributorAsync(idnoSuffix: "001", caemCode: "99.00", cfojCode: null);

        var result = await harness.Guard.ScanAsync("CAEM", "01.11");

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferencingRowCount.Should().Be(0);
        result.Value.ReferencingEntities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new ClassifierReferencingEntityDto(nameof(Contributor), 0L));
    }

    /// <summary>
    /// R0402 — Two contributors carrying the target CAEM code make the total
    /// count 2, and the per-entity row reports the same. Mixed-case scheme
    /// strings still resolve to the same canonical entry.
    /// </summary>
    [Fact]
    public async Task ScanAsync_KnownSchemeWithReferences_CountsRows_AndCanonicalizesScheme()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idnoSuffix: "002", caemCode: "01.11", cfojCode: null);
        await harness.SeedContributorAsync(idnoSuffix: "003", caemCode: "01.11", cfojCode: null);
        await harness.SeedContributorAsync(idnoSuffix: "004", caemCode: "01.12", cfojCode: null);

        var result = await harness.Guard.ScanAsync(schemeCode: "caem", value: "01.11");

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferencingRowCount.Should().Be(2);
        result.Value.SchemeCode.Should().Be("CAEM"); // canonical upper-case
        result.Value.ReferencingEntities.Should().ContainSingle()
            .Which.Count.Should().Be(2);
    }

    /// <summary>
    /// R0402 — Schemes other than <c>CAEM</c> are mapped too. <c>CFOJ</c>
    /// counts citing <see cref="Contributor.CfojCode"/> rows.
    /// </summary>
    [Fact]
    public async Task ScanAsync_CfojScheme_CountsContributorCfojCodeReferences()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idnoSuffix: "005", caemCode: null, cfojCode: "SRL");
        await harness.SeedContributorAsync(idnoSuffix: "006", caemCode: null, cfojCode: "SRL");

        var result = await harness.Guard.ScanAsync("CFOJ", "SRL");

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferencingRowCount.Should().Be(2);
    }

    // ─────────────────── ClassifierService.DeactivateAsync wiring ───────────────────

    /// <summary>
    /// R0402 — Deactivating an unreferenced classifier succeeds and flips
    /// <c>IsActive=false</c>.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_NoReferences_FlipsRowToInactive()
    {
        var harness = Harness.Create();
        await harness.SeedClassifierAsync(kind: "CAEM", code: "ORPHAN.01");

        var result = await harness.ClassifierService.DeactivateAsync("CAEM", "ORPHAN.01");

        result.IsSuccess.Should().BeTrue();
        var row = harness.Db.Classifiers.Single(c => c.Kind == "CAEM" && c.Code == "ORPHAN.01");
        row.IsActive.Should().BeFalse();
        row.UpdatedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>
    /// R0402 / TOR CF 17.09 — Deactivating a classifier still cited by a
    /// contributor short-circuits with
    /// <see cref="ErrorCodes.ClassifierReferenced"/> and leaves the row
    /// unchanged.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_ReferencedByContributor_ReturnsClassifierReferencedConflict()
    {
        var harness = Harness.Create();
        await harness.SeedClassifierAsync(kind: "CAEM", code: "01.11");
        await harness.SeedContributorAsync(idnoSuffix: "010", caemCode: "01.11", cfojCode: null);

        var result = await harness.ClassifierService.DeactivateAsync("CAEM", "01.11");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ClassifierReferenced);
        var row = harness.Db.Classifiers.Single(c => c.Kind == "CAEM" && c.Code == "01.11");
        row.IsActive.Should().BeTrue("the row must remain active when references would be orphaned");
    }

    /// <summary>
    /// R0402 — Deactivating a non-existent row reports
    /// <see cref="ErrorCodes.NotFound"/> WITHOUT consulting the guard.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_UnknownRow_ReturnsNotFound()
    {
        var harness = Harness.Create();

        var result = await harness.ClassifierService.DeactivateAsync("CAEM", "DOES.NOT.EXIST");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────────── Test harness ───────────────────────────

    /// <summary>Deterministic clock; one instant for every test.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUTs + DB so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ClassifierReferenceGuard Guard { get; init; }
        public required IClassifierService ClassifierService { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            // CnasDbContext implements BOTH ICnasDbContext + IReadOnlyCnasDbContext.
            IReadOnlyCnasDbContext readDb = db;
            var guard = new ClassifierReferenceGuard(readDb);
            var classifierService = new ClassifierService(db, new StubClock(ClockNow), guard);
            return new Harness
            {
                Db = db,
                Guard = guard,
                ClassifierService = classifierService,
            };
        }

        public async Task SeedContributorAsync(string idnoSuffix, string? caemCode, string? cfojCode)
        {
            Db.Contributors.Add(new Contributor
            {
                Idno = $"100000000{idnoSuffix.PadLeft(4, '0')}",
                IdnoHash = $"hash-{idnoSuffix}",
                Denumire = $"Contributor {idnoSuffix}",
                CaemCode = caemCode,
                CfojCode = cfojCode,
                RegisteredAtUtc = ClockNow.AddYears(-1),
                CreatedAtUtc = ClockNow.AddYears(-1),
                IsActive = true,
                IsDeactivated = false,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedClassifierAsync(string kind, string code)
        {
            Db.Classifiers.Add(new Classifier
            {
                Kind = kind,
                Code = code,
                LabelRo = $"{kind}/{code}",
                Source = "internal",
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-classifier-guard-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
