using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC03 — "Caut/vizualizez date". End-to-end journey covering the authorized-user search
/// and view paths into the two main registries per TOR §3.3 / CF 03.01–03.14:
/// <list type="bullet">
///   <item>Contributor lookup by full IDNO — <c>GET /api/contributors?q={idno}</c>.
///         Exercises the hash-branch lookup over the encrypted <see cref="Contributor.Idno"/>
///         column via the <see cref="Contributor.IdnoHash"/> shadow column (SEC 035).</item>
///   <item>Insured-person lookup by full IDNP — <c>GET /api/insured-persons/by-idnp/{idnp}</c>.
///         Exercises the parallel hash-branch on <see cref="InsuredPerson.IdnpHash"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> CNAS operator — authenticated via <see cref="TestAuthHandler"/> with the
/// <c>cnas-user</c> role. CF 03.01 reserves the search surface to authorised users;
/// anonymous requests would be rejected at the <c>[Authorize]</c> guard on both
/// controllers, so the journey runs under the authenticated fixture with the persona
/// header set.
/// </para>
/// <para>
/// <b>Why exercise the hash-branch specifically.</b> The plaintext IDNO/IDNP columns are
/// encrypted at rest by <c>EncryptedStringConverter</c> with a different IV per row, so a
/// SQL <c>WHERE Idno = 'X'</c> against the ciphertext would never match. Both registry
/// services therefore canonicalise + hash the query and equality-match the shadow hash
/// column. This is the substring-search fix #73 was about — locking the hash-branch
/// behaviour over HTTP guarantees the contract survives future refactors of the encryption
/// machinery.
/// </para>
/// <para>
/// <b>Distinct from UC12a/UC12b.</b> Those journeys assert the <i>register-then-find</i>
/// flow (write + read in one shot). UC03 is the read-only "I have an existing record,
/// help me view it" use case — closer to the citizen-service inquiry an examiner gets via
/// phone. The seeded rows are inserted directly through the DbContext (bypassing the
/// registration controller) so the journey is decoupled from the register-side audit
/// behaviour and tests the search/view surface in isolation.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc03_SearchViewDataJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture supplying the running Kestrel host.</param>
    public Uc03_SearchViewDataJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A CNAS operator searches the Contributor registry by a full 13-digit IDNO and the
    /// hash-branch lookup returns the seeded row exactly once. Verifies the encrypted-IDNO
    /// equality contract over HTTP.
    /// </summary>
    [Fact]
    public async Task Contributor_SearchByFullIdno_ReturnsSeededRowViaHashBranch()
    {
        // Arrange — seed a contributor with a known IDNO + matching shadow hash.
        const string idno = "1003600054321"; // Plain 13-digit numeric — IDNO format only requires the 13-digit shape for searches.
        const string denumire = "UC03 Search Contributor SRL";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var contributor = new Contributor
        {
            Idno = idno,
            IdnoHash = hasher.ComputeHash(idno),
            Denumire = denumire,
            CfojCode = "1170",
            CaemCode = "47111",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Contributors.Add(contributor);
        await db.SaveChangesAsync();
        var expectedSqid = sqids.Encode(contributor.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        var operatorSqid = sqids.Encode(130_001);
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: operatorSqid, Roles: ["cnas-user"])));

        // Act — full 13-digit query triggers the hash branch in ContributorService.SearchAsync.
        using var response = await client.GetAsync(
            $"/api/contributors?q={idno}&page=1&pageSize=10");

        // Assert
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var page = await response.Content.ReadFromJsonAsync<PagedResult<ContributorListItem>>();
        page.Should().NotBeNull();
        page!.Items.Should().ContainSingle(c => c.Id == expectedSqid,
            "the hash-branch lookup must return the seeded contributor exactly once");
        page.Items.Single(c => c.Id == expectedSqid).Denumire.Should().Be(denumire);

        // GET by Sqid id — the view surface CF 03.11 promises ("vizualizarea detaliilor").
        using var viewResponse = await client.GetAsync($"/api/contributors/{expectedSqid}");
        viewResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await viewResponse.Content.ReadAsStringAsync());

        var view = await viewResponse.Content.ReadFromJsonAsync<ContributorOutput>();
        view.Should().NotBeNull();
        view!.Id.Should().Be(expectedSqid, "the Sqid round-trip must preserve the id");
        view.Idno.Should().Be(idno, "the encrypted plaintext must decrypt for the operator view");
        view.Denumire.Should().Be(denumire);
    }

    /// <summary>
    /// A CNAS operator looks up an insured person by full IDNP via the dedicated
    /// <c>by-idnp</c> endpoint and the hash-branch returns the seeded row. Asserts the
    /// parallel of the contributor lookup over the Annex-2 registry.
    /// </summary>
    [Fact]
    public async Task InsuredPerson_GetByFullIdnp_ReturnsSeededRowViaHashBranch()
    {
        // Arrange — seed an insured person with a valid IDNP + matching shadow hash. The
        // IDNP must satisfy Idnp.TryCreate (mod-10 weighted-{7,3,1} checksum) because the
        // by-idnp endpoint validates the route argument through that value object.
        // Picked so the mod-10 weighted-{7,3,1} checksum holds AND so it does not collide
        // with any IDNP already seeded by UC02 / UC04 / UC06 / UC12b / UC13 / UC17 / UC21.
        const string idnp = "2000111111110";
        const string lastName = "UcZeroThree";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var person = new InsuredPerson
        {
            Idnp = idnp,
            IdnpHash = hasher.ComputeHash(idnp),
            LastName = lastName,
            FirstName = "Ion",
            Patronymic = "Vasilevici",
            BirthDate = new DateOnly(1980, 5, 12),
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.InsuredPersons.Add(person);
        await db.SaveChangesAsync();
        var expectedSqid = sqids.Encode(person.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        var operatorSqid = sqids.Encode(130_002);
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: operatorSqid, Roles: ["cnas-user"])));

        // Act — view by IDNP through the dedicated lookup endpoint (hash branch on InsuredPerson.IdnpHash).
        using var response = await client.GetAsync($"/api/insured-persons/by-idnp/{idnp}");

        // Assert
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var view = await response.Content.ReadFromJsonAsync<InsuredPersonOutput>();
        view.Should().NotBeNull();
        view!.Id.Should().Be(expectedSqid, "the by-IDNP lookup must round-trip the same Sqid id as the row");
        view.Idnp.Should().Be(idnp);
        view.LastName.Should().Be(lastName);
        view.FirstName.Should().Be("Ion");
        view.BirthDate.Should().Be(new DateOnly(1980, 5, 12));

        // Substring name search must also return the seeded row — locks CF 03.02
        // "criterii flexibile" + CF 03.13 (case-insensitive substring) for the
        // unencrypted name columns.
        using var nameSearchResponse = await client.GetAsync(
            $"/api/insured-persons?query={lastName}&page=1&pageSize=10");
        nameSearchResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await nameSearchResponse.Content.ReadAsStringAsync());

        var namePage = await nameSearchResponse.Content
            .ReadFromJsonAsync<PagedResult<InsuredPersonListItem>>();
        namePage.Should().NotBeNull();
        namePage!.Items.Should().Contain(p => p.Id == expectedSqid,
            "the name-substring search must surface the seeded insured person (CF 03.02 / CF 03.13)");
    }
}
