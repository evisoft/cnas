using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="PublicContentService"/>. Uses EF Core InMemory and
/// NSubstitute for the surrounding collaborators. The
/// <c>EF.Functions.ILike</c>-vs-Contains seam is locked end-to-end by
/// <c>Uc01_PublicContentJourneyTests</c>; this file targets the in-process search behaviour
/// — specifically the R0162 / CF 03.13 diacritic-insensitive contract added on top of the
/// case-insensitive baseline.
/// </summary>
public class PublicContentServiceTests
{
    /// <summary>Deterministic clock used so audit/snapshot fields stay stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── R0162 — diacritic-insensitive search ───────────────────────

    /// <summary>
    /// R0162 / CF 03.13 — an ASCII query (e.g. <c>"Pensii"</c>) must match a
    /// diacritic-bearing <c>NameRo</c> (e.g. <c>"Pensii și Indemnizații"</c>). On the
    /// Postgres path this routes through <c>unaccent(col)</c>; on the InMemory provider
    /// the service folds both sides with
    /// <see cref="Application.Search.DiacriticFolding"/>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AsciiQuery_MatchesDiacriticNameRo()
    {
        var harness = Harness.Create();
        await harness.SeedPassportAsync(
            code: "SP-A",
            nameRo: "Pensii și Indemnizații",
            descriptionRo: "Conținut despre pensii.");
        await harness.SeedPassportAsync(
            code: "SP-B",
            nameRo: "Alocații Sociale",
            descriptionRo: "Conținut despre alocații.");

        var request = new SearchRequest(
            Query: "Pensii",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Title.Should().Be("Pensii și Indemnizații");
    }

    /// <summary>
    /// R0162 / CF 03.13 — a diacritic query must match a plain-ASCII <c>DescriptionRo</c>.
    /// Insensitivity must be symmetric: the fold normalises both sides to the same
    /// canonical form. Targets the <c>DescriptionRo</c> column to prove both folded
    /// columns are exercised.
    /// </summary>
    [Fact]
    public async Task SearchAsync_DiacriticQuery_MatchesAsciiDescriptionRo()
    {
        var harness = Harness.Create();
        await harness.SeedPassportAsync(
            code: "SP-A",
            nameRo: "Serviciu Social A",
            descriptionRo: "Indemnizatii pentru cetateni");
        await harness.SeedPassportAsync(
            code: "SP-B",
            nameRo: "Serviciu Social B",
            descriptionRo: "Servicii diverse non-corelate");

        var request = new SearchRequest(
            Query: "indemnizații",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Title.Should().Be("Serviciu Social A");
    }

    // ─────────────────────── R0164 — wildcard mask filters (UI 012 / CF 03.02) ───────────────────────

    /// <summary>
    /// R0164 / UI 012 / CF 03.02 — a wildcard query <c>Pensii*</c> ("starts with
    /// Pensii") must match only service passports whose <c>NameRo</c> begins with the
    /// literal substring. The explicit-wildcard branch must defeat the implicit
    /// <c>%...%</c> wrap that would otherwise also surface descriptions containing
    /// the term in the middle of a longer phrase.
    /// </summary>
    [Fact]
    public async Task SearchAsync_StarSuffixMask_MatchesStartsWith()
    {
        var harness = Harness.Create();
        await harness.SeedPassportAsync(
            code: "SP-A",
            nameRo: "Pensii și Indemnizații",
            descriptionRo: "Conținut despre pensii.");
        await harness.SeedPassportAsync(
            code: "SP-B",
            nameRo: "Indemnizații pentru Pensii",
            descriptionRo: "Conținut despre indemnizații.");
        await harness.SeedPassportAsync(
            code: "SP-C",
            nameRo: "Alocații Sociale",
            descriptionRo: "Conținut despre alocații.");

        var request = new SearchRequest(
            Query: "Pensii*",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Title.Should().Be("Pensii și Indemnizații");
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-publiccontent-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required PublicContentService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var service = new PublicContentService(db, sqids);
            return new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
            };
        }

        /// <summary>Inserts an enabled, active <see cref="ServicePassport"/> with sane defaults.</summary>
        public async Task<ServicePassport> SeedPassportAsync(
            string code,
            string nameRo,
            string descriptionRo)
        {
            var entity = new ServicePassport
            {
                Code = code,
                NameRo = nameRo,
                DescriptionRo = descriptionRo,
                WorkflowCode = "wf-test",
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
