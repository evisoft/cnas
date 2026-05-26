using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
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
/// Integration tests for <see cref="DataSearchService"/> (UC03 / UC12 registry search).
/// Uses EF Core InMemory for the persistence backend and NSubstitute for the surrounding
/// collaborators (sqids). Exercises BUG-007b — the provider-aware seam that lets the
/// service's <c>EF.Functions.ILike</c> branches stay native PostgreSQL ILIKE in production
/// while remaining executable against EF Core InMemory in integration tests (same pattern
/// as <see cref="PublicContentService"/>'s <c>IsRelationalProvider</c> seam, and the
/// per-class seam in <see cref="ContributorService"/> / <see cref="InsuredPersonService"/>).
/// </summary>
public class DataSearchServiceTests
{
    /// <summary>Deterministic clock instant — keeps audit/snapshot fields stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── SearchAsync — CONTRIBUTORS ───────────────────────

    /// <summary>
    /// Regression for BUG-007b on the <c>CONTRIBUTORS</c> branch: a non-empty <c>?query=</c>
    /// must NOT throw on the InMemory provider. Seeds two contributors (one matching, one
    /// not) and asserts only the match is returned with a case-insensitive substring filter
    /// over <c>Denumire || Idno</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Contributors_InMemory_Filters_CaseInsensitive()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: "1003600012346", denumire: "Alpha SRL");
        await harness.SeedContributorAsync(idno: "2000000000006", denumire: "Beta SRL");

        var request = new SearchRequest(
            Query: "alpha",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("CONTRIBUTORS", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["denumire"].Should().Be("Alpha SRL");
        // Sqid-encoded ID, never a raw int.
        row.Id.Should().StartWith("SQID-");
    }

    // ─────────────────────── SearchAsync — INSURED ───────────────────────

    /// <summary>
    /// Regression for BUG-007b on the <c>INSURED</c> branch: a non-empty <c>?query=</c>
    /// must NOT throw on the InMemory provider. Seeds two insured persons (one matching,
    /// one not) and asserts only the match is returned with a case-insensitive substring
    /// filter over <c>Idnp || LastName || FirstName</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Insured_InMemory_Filters_CaseInsensitive()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: "1003600012346", lastName: "Alpha", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: "2000000000006", lastName: "Beta", firstName: "Boris");

        var request = new SearchRequest(
            Query: "ALPHA",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("INSURED", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["lastName"].Should().Be("Alpha");
        row.Columns["firstName"].Should().Be("Ana");
        row.Id.Should().StartWith("SQID-");
    }

    // ─────────────────────── SearchAsync — APPLICATIONS ───────────────────────

    /// <summary>
    /// Regression for BUG-007b on the <c>APPLICATIONS</c> branch: a non-empty <c>?query=</c>
    /// must NOT throw on the InMemory provider. Seeds two applications (one matching, one
    /// not — and an extra row with a <c>null</c> ReferenceNumber to prove the guard still
    /// holds) and asserts only the case-insensitive substring match is returned.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Applications_InMemory_Filters_CaseInsensitive()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(referenceNumber: "REF-ALPHA-001");
        await harness.SeedApplicationAsync(referenceNumber: "REF-BETA-002");
        await harness.SeedApplicationAsync(referenceNumber: null);

        var request = new SearchRequest(
            Query: "alpha",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("APPLICATIONS", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["ref"].Should().Be("REF-ALPHA-001");
        row.Id.Should().StartWith("SQID-");
    }

    // ─────────────────────── R0162 — diacritic-insensitive search ───────────────────────

    /// <summary>
    /// R0162 / CF 03.13 — <c>CONTRIBUTORS</c> branch: an ASCII query must match a
    /// diacritic-bearing <c>Denumire</c>. The Postgres path routes through
    /// <c>unaccent(col)</c>; the InMemory provider folds both sides with
    /// <see cref="Application.Search.DiacriticFolding"/>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Contributors_DiacriticInsensitive_Matches()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: "1003600012346", denumire: "Țăranu SRL");
        await harness.SeedContributorAsync(idno: "2000000000006", denumire: "Popescu SRL");

        var request = new SearchRequest(
            Query: "Taranu",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("CONTRIBUTORS", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["denumire"].Should().Be("Țăranu SRL");
    }

    /// <summary>
    /// R0162 / CF 03.13 — <c>INSURED</c> branch: an ASCII query must match a
    /// diacritic-bearing <c>LastName</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Insured_DiacriticInsensitive_Matches()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: "1003600012346", lastName: "Ștefan", firstName: "Andrei");
        await harness.SeedInsuredAsync(idnp: "2000000000006", lastName: "Popescu", firstName: "Ion");

        var request = new SearchRequest(
            Query: "Stefan",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("INSURED", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["lastName"].Should().Be("Ștefan");
    }

    /// <summary>
    /// R0162 / CF 03.13 — <c>INSURED</c> branch: a diacritic query must match a
    /// plain-ASCII <c>LastName</c>. Symmetric to <see cref="SearchAsync_Insured_DiacriticInsensitive_Matches"/>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Insured_DiacriticQuery_MatchesAscii()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: "1003600012346", lastName: "Stefan", firstName: "Andrei");
        await harness.SeedInsuredAsync(idnp: "2000000000006", lastName: "Popescu", firstName: "Ion");

        var request = new SearchRequest(
            Query: "Ștefan",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("INSURED", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.Columns["lastName"].Should().Be("Stefan");
    }

    // ─────────────────────── R0164 — wildcard mask filters (UI 012 / CF 03.02) ───────────────────────

    /// <summary>
    /// R0164 / UI 012 / CF 03.02 — a <c>CONTRIBUTORS</c> wildcard query <c>*escu</c>
    /// ("ends with escu") must match only contributors whose <c>Denumire</c> terminates
    /// with the literal substring. The explicit-wildcard branch must defeat the implicit
    /// <c>%...%</c> wrap that would otherwise surface <c>"Popescu Ion"</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Contributors_StarPrefixMask_MatchesEndsWith()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: "1003600012346", denumire: "Popescu");
        await harness.SeedContributorAsync(idno: "2000000000006", denumire: "Popovescu");
        await harness.SeedContributorAsync(idno: "1003600099996", denumire: "Ion");

        var request = new SearchRequest(
            Query: "*escu",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("CONTRIBUTORS", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(i => i.Columns["denumire"])
            .Should().BeEquivalentTo(["Popescu", "Popovescu"]);
    }

    /// <summary>
    /// R0164 / UI 012 / CF 03.02 — an <c>INSURED</c> wildcard query <c>*escu</c>
    /// ("ends with escu") must match only insured persons whose <c>LastName</c> ends
    /// with the literal substring. Mirrors the <c>CONTRIBUTORS</c> sibling.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Insured_StarPrefixMask_MatchesEndsWith()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: "1003600012346", lastName: "Popescu", firstName: "Ion");
        await harness.SeedInsuredAsync(idnp: "2000000000006", lastName: "Popovescu", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: "1003600099996", lastName: "Ionel", firstName: "Marin");

        var request = new SearchRequest(
            Query: "*escu",
            Filters: null,
            Mask: null,
            SortBy: null,
            SortDescending: false,
            Page: new PageRequest(1, 10));

        var result = await harness.Service.SearchAsync("INSURED", request);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(i => i.Columns["lastName"])
            .Should().BeEquivalentTo(["Popescu", "Popovescu"]);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-datasearch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DataSearchService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var service = new DataSearchService(db, sqids);
            return new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
            };
        }

        /// <summary>Inserts an active <see cref="Contributor"/> with sane defaults and returns the entity.</summary>
        public async Task<Contributor> SeedContributorAsync(
            string idno,
            string denumire)
        {
            var entity = new Contributor
            {
                Idno = idno,
                IdnoHash = IdHashHelper.Hash(idno),
                Denumire = denumire,
                CfojCode = null,
                CaemCode = null,
                IsInsolvent = false,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Contributors.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }

        /// <summary>Inserts an active <see cref="InsuredPerson"/> with sane defaults and returns the entity.</summary>
        public async Task<InsuredPerson> SeedInsuredAsync(
            string idnp,
            string lastName,
            string firstName)
        {
            var entity = new InsuredPerson
            {
                Idnp = idnp,
                IdnpHash = IdHashHelper.Hash(idnp),
                LastName = lastName,
                FirstName = firstName,
                Patronymic = null,
                BirthDate = new DateOnly(1980, 1, 1),
                IsDeceased = false,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.InsuredPersons.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }

        /// <summary>Inserts an active <see cref="ServiceApplication"/> with sane defaults and returns the entity.</summary>
        public async Task<ServiceApplication> SeedApplicationAsync(string? referenceNumber)
        {
            var entity = new ServiceApplication
            {
                SolicitantId = 1,
                ServicePassportId = 1,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                ReferenceNumber = referenceNumber,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            };
            Db.Applications.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
