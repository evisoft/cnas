using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// Integration tests for <see cref="PostgresGlobalSearchService"/>
/// (R0160 / R0161 / TOR CF 03.03). Uses the EF Core InMemory provider so the
/// service exercises its substring-rank fallback branch — no real Postgres
/// required.
/// </summary>
public sealed class PostgresGlobalSearchServiceTests
{
    /// <summary>Deterministic clock instant — keeps audit / snapshot fields stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── validation ───────────────────────

    /// <summary>Empty / whitespace query surfaces VALIDATION_FAILED — never throws.</summary>
    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("REF-001");

        var input = new GlobalSearchInputDto(Query: "   ", Domains: null, Skip: 0, Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── happy-path single-domain match ───────────────────────

    /// <summary>Query against a seeded application returns the matching row only.</summary>
    [Fact]
    public async Task SearchAsync_Applications_ReturnsMatchingHit()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("REF-ALPHA-001");
        await harness.SeedApplicationAsync("REF-BETA-002");

        var input = new GlobalSearchInputDto(
            Query: "ALPHA",
            Domains: new[] { GlobalSearchDomains.Applications },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(1);
        var hit = result.Value.Results.Should().ContainSingle().Subject;
        hit.Domain.Should().Be(GlobalSearchDomains.Applications);
        hit.Title.Should().Be("REF-ALPHA-001");
        hit.Sqid.Should().StartWith("SQID-");
        hit.Rank.Should().BeGreaterThan(0d);
    }

    // ─────────────────────── domains filter restricts results ───────────────────────

    /// <summary>
    /// When <c>Domains</c> restricts the search to a single domain, matches in
    /// other domains do NOT appear in the result list.
    /// </summary>
    [Fact]
    public async Task SearchAsync_DomainsFilter_RestrictsToSelectedDomain()
    {
        var harness = Harness.Create();
        // Seed a contributor + an application that BOTH carry the term "alpha".
        await harness.SeedContributorAsync(idno: "1003600012346", denumire: "Alpha SRL");
        await harness.SeedApplicationAsync("REF-ALPHA-001");

        // Restrict to contributors only.
        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: new[] { GlobalSearchDomains.Contributors },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(1);
        result.Value.Results.Should().OnlyContain(r => r.Domain == GlobalSearchDomains.Contributors);
    }

    // ─────────────────────── full-fan-out: empty Domains = all ───────────────────────

    /// <summary>
    /// Empty / null Domains expands to every canonical domain — confirmed by
    /// seeding rows in two domains and asserting both appear in the response.
    /// </summary>
    [Fact]
    public async Task SearchAsync_EmptyDomains_FansOutAcrossAllDomains()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: "1003600012346", denumire: "Alpha SRL");
        await harness.SeedInsuredAsync(idnp: "2003600012346", lastName: "Alpha", firstName: "Andrei");

        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: null,
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().BeGreaterOrEqualTo(2);
        result.Value.Results.Select(r => r.Domain).Should()
            .Contain(GlobalSearchDomains.Contributors)
            .And.Contain(GlobalSearchDomains.InsuredPersons);
    }

    // ─────────────────────── skip/take paging ───────────────────────

    /// <summary>Skip / Take are applied to the merged + globally-ranked hit list.</summary>
    [Fact]
    public async Task SearchAsync_SkipTake_AppliesAfterGlobalSort()
    {
        var harness = Harness.Create();
        for (var i = 0; i < 5; i++)
        {
            await harness.SeedApplicationAsync($"REF-ALPHA-{i:D3}");
        }

        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: new[] { GlobalSearchDomains.Applications },
            Skip: 2,
            Take: 2);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(5);  // grand total BEFORE paging
        result.Value.Results.Should().HaveCount(2);  // page size
        result.Value.Skip.Should().Be(2);
        result.Value.Take.Should().Be(2);
    }

    // ─────────────────────── empty results ───────────────────────

    /// <summary>A query that matches nothing returns an empty hit list with zero TotalHits.</summary>
    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmpty()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("REF-ALPHA-001");

        var input = new GlobalSearchInputDto(
            Query: "no-such-token-zzz",
            Domains: new[] { GlobalSearchDomains.Applications },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(0);
        result.Value.Results.Should().BeEmpty();
    }

    // ─────────────────────── inactive rows excluded ───────────────────────

    /// <summary>Soft-deleted rows (IsActive=false) are excluded from the result list.</summary>
    [Fact]
    public async Task SearchAsync_InactiveRows_Excluded()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("REF-ALPHA-001", isActive: false);

        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: new[] { GlobalSearchDomains.Applications },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(0);
    }

    // ─────────────────────── insured-persons branch ───────────────────────

    /// <summary>Insured-person hit projects "LastName FirstName" as title and Idnp as snippet.</summary>
    [Fact]
    public async Task SearchAsync_InsuredPersons_ProjectsTitleAndIdnpSnippet()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: "2003600012346", lastName: "Alpha", firstName: "Andrei");

        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: new[] { GlobalSearchDomains.InsuredPersons },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var hit = result.Value.Results.Should().ContainSingle().Subject;
        hit.Title.Should().Be("Alpha Andrei");
        hit.Snippet.Should().Be("2003600012346");
    }

    // ─────────────────────── documents branch ───────────────────────

    /// <summary>Document hit matches on Title.</summary>
    [Fact]
    public async Task SearchAsync_Documents_MatchesOnTitle()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(title: "Alpha decision");

        var input = new GlobalSearchInputDto(
            Query: "alpha",
            Domains: new[] { GlobalSearchDomains.Documents },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().ContainSingle()
            .Which.Title.Should().Be("Alpha decision");
    }

    // ─────────────────────── dossiers branch ───────────────────────

    /// <summary>Dossier hit matches on DossierNumber.</summary>
    [Fact]
    public async Task SearchAsync_Dossiers_MatchesOnDossierNumber()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync(dossierNumber: "DOS-ALPHA-001");

        var input = new GlobalSearchInputDto(
            Query: "ALPHA",
            Domains: new[] { GlobalSearchDomains.Dossiers },
            Skip: 0,
            Take: 10);

        var result = await harness.Service.SearchAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var hit = result.Value.Results.Should().ContainSingle().Subject;
        hit.Domain.Should().Be(GlobalSearchDomains.Dossiers);
        hit.Title.Should().Be("DOS-ALPHA-001");
    }

    // ─────────────────────── test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>A new context whose IsRelationalProvider check returns <see langword="false"/>.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-globalsearch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required PostgresGlobalSearchService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        /// <returns>A wired harness ready for seeding.</returns>
        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var service = new PostgresGlobalSearchService(db, sqids);
            return new Harness { Db = db, Service = service, Sqids = sqids };
        }

        /// <summary>Inserts an active <see cref="ServiceApplication"/> with sane defaults.</summary>
        /// <param name="referenceNumber">Reference-number value to match against.</param>
        /// <param name="isActive">Soft-delete flag (true = visible).</param>
        /// <returns>The seeded entity.</returns>
        public async Task<ServiceApplication> SeedApplicationAsync(string? referenceNumber, bool isActive = true)
        {
            var entity = new ServiceApplication
            {
                SolicitantId = 1,
                ServicePassportId = 1,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                ReferenceNumber = referenceNumber,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = isActive,
            };
            Db.Applications.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }

        /// <summary>Inserts an active <see cref="Contributor"/> with sane defaults.</summary>
        /// <param name="idno">Idno value.</param>
        /// <param name="denumire">Denumire (display name) value.</param>
        /// <returns>The seeded entity.</returns>
        public async Task<Contributor> SeedContributorAsync(string idno, string denumire)
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

        /// <summary>Inserts an active <see cref="InsuredPerson"/> with sane defaults.</summary>
        /// <param name="idnp">Idnp value.</param>
        /// <param name="lastName">Last-name value.</param>
        /// <param name="firstName">First-name value.</param>
        /// <returns>The seeded entity.</returns>
        public async Task<InsuredPerson> SeedInsuredAsync(string idnp, string lastName, string firstName)
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

        /// <summary>Inserts an active <see cref="Document"/> with sane defaults.</summary>
        /// <param name="title">Document title.</param>
        /// <returns>The seeded entity.</returns>
        public async Task<Document> SeedDocumentAsync(string title)
        {
            var entity = new Document
            {
                DossierId = null,
                Kind = DocumentKind.Decision,
                Title = title,
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"docs/{Guid.NewGuid():N}.pdf",
                StorageBucket = "cnas-docs",
                ContentSha256Hex = new string('0', 64),
                IsSigned = false,
                VerdictNote = null,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            };
            Db.Documents.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }

        /// <summary>Inserts an active <see cref="Dossier"/> with sane defaults.</summary>
        /// <param name="dossierNumber">Dossier-number value.</param>
        /// <returns>The seeded entity.</returns>
        public async Task<Dossier> SeedDossierAsync(string dossierNumber)
        {
            var entity = new Dossier
            {
                ApplicationId = 1,
                DossierNumber = dossierNumber,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            };
            Db.Dossiers.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
