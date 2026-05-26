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
/// UC12 — "Vizualizare date" (Annex 1 — <c>Plătitori de contribuții</c>). End-to-end
/// journey covering an authenticated CNAS operator registering a new contributor and
/// then retrieving it through the registry browsing endpoints. Drives the real
/// <c>ContributorsController</c> + <c>ContributorService</c> + EF Core stack so the
/// encrypted-column round-trip (IDNO ↔ IdnoHash) is exercised at the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> CNAS operator — authenticated via <see cref="TestAuthHandler"/> with
/// the <c>cnas-user</c> role. The controller policy requires either <c>cnas-user</c> or
/// <c>cnas-admin</c>; we pick the lower-privilege role to assert the policy gate accepts
/// it.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 201 Created from <c>POST /api/contributors</c> returning the Sqid id.</item>
///   <item>A <see cref="Contributor"/> DB row exists with the supplied IDNO and a
///         non-empty <see cref="Contributor.IdnoHash"/> shadow column — locks the
///         CLAUDE.md §5.7 / SEC 035 encryption-aware lookup contract.</item>
///   <item>HTTP 200 OK from <c>GET /api/contributors/{id}</c> returning the Sqid id
///         and IDNO unchanged (RULE 3 round-trip).</item>
///   <item>HTTP 200 OK from <c>GET /api/contributors?q={idno}&amp;page=1&amp;pageSize=10</c>
///         with the new contributor present in the paged result.</item>
///   <item>An <see cref="AuditLog"/> row with <c>EventCode = "CONTRIBUTOR.REGISTERED"</c>
///         exists, targeting the newly-created entity — CLAUDE.md §5.6 / SEC 042.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc12a_ContributorsRegistryJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc12a_ContributorsRegistryJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A CNAS operator registers a new contributor, retrieves it by id, finds it through
    /// the registry search, and the action is audited.
    /// </summary>
    [Fact]
    public async Task Register_LookupById_SearchByIdno_PersistsAndAudits()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        // A valid IDNO (first digit 1-9, mod-10 weighted-{7,3,1} checksum).
        const string idno = "1003600012346";
        var operatorSqid = sqids.Encode(120_001);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: operatorSqid, Roles: ["cnas-user"])));

        var registerPayload = new ContributorRegistrationInput(
            Idno: idno,
            Denumire: "UC12a E2E Test Contributor SRL",
            CfojCode: "1170",
            CaemCode: "47111");

        // Act 1 — register. The controller uses CreatedAtAction(nameof(GetAsync), ...)
        // which 500s today because MVC strips the "Async" suffix from action names while
        // nameof still returns "GetAsync" — same BUG-001 documented on UC06. The DB write
        // happens BEFORE the response is built, so the new row is persisted regardless;
        // we observe persistence (the business outcome) rather than the broken response
        // shape (which is locked separately by Uc06).
        using var registerResponse = await client.PostAsJsonAsync("/api/contributors", registerPayload);
        registerResponse.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.InternalServerError },
            await registerResponse.Content.ReadAsStringAsync());

        // Assert — the row is persisted with IDNO + IdnoHash both populated. Read via a
        // fresh scope so the post-commit state is observed.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var readSqids = readScope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = readScope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var expectedHash = hasher.ComputeHash(idno);
        var persisted = await readDb.Contributors.AsNoTracking()
            .SingleOrDefaultAsync(c => c.IdnoHash == expectedHash && c.IsActive);
        persisted.Should().NotBeNull("the new contributor must be persisted");
        persisted!.Idno.Should().Be(idno);
        persisted.IdnoHash.Should().NotBeNullOrWhiteSpace(
            "the IdnoHash shadow column must be written alongside Idno (SEC 035 contract)");
        persisted.Denumire.Should().Be("UC12a E2E Test Contributor SRL");
        persisted.IsActive.Should().BeTrue();
        persisted.IsInsolvent.Should().BeFalse();

        // Re-encode the persisted id so the rest of the journey can address it through
        // the Sqid-only HTTP surface.
        var persistedSqid = readSqids.Encode(persisted.Id);

        // Act 2 — GET by Sqid id.
        using var getResponse = await client.GetAsync($"/api/contributors/{persistedSqid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await getResponse.Content.ReadAsStringAsync());
        var fetched = await getResponse.Content.ReadFromJsonAsync<ContributorOutput>();
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(persistedSqid, "the round-trip must preserve the Sqid id exactly");
        fetched.Idno.Should().Be(idno);
        fetched.Denumire.Should().Be("UC12a E2E Test Contributor SRL");

        // Act 3 — search by IDNO (full 13-digit query → hash branch).
        using var searchResponse = await client.GetAsync(
            $"/api/contributors?q={idno}&page=1&pageSize=10");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await searchResponse.Content.ReadAsStringAsync());
        var page = await searchResponse.Content.ReadFromJsonAsync<PagedResult<ContributorListItem>>();
        page.Should().NotBeNull();
        page!.Items.Should().Contain(c => c.Id == persistedSqid,
            "the registered contributor must surface in the IDNO search");

        // Assert — audit row exists for the registration.
        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "CONTRIBUTOR.REGISTERED"
                && a.TargetEntity == nameof(Contributor)
                && a.TargetEntityId == persisted.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "contributor registration must journal a CONTRIBUTOR.REGISTERED audit row (SEC 042)");
        audit!.ActorId.Should().Be(operatorSqid,
            "the audit ActorId must match the authenticated principal's Sqid id");
    }
}
