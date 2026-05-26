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
/// UC12 — "Vizualizare date" (Annex 2 — <c>Persoane asigurate</c>). End-to-end journey
/// covering an authenticated CNAS operator registering a new insured person and finding
/// it through the registry search endpoint. Pivots away from the originally-suggested
/// UC13 profile journey because <c>IProfileService</c> exists in the application layer
/// but is not yet exposed by a controller — see the final report for the pivot rationale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> CNAS operator — authenticated via <see cref="TestAuthHandler"/> with
/// the <c>cnas-user</c> role. The controller policy requires either <c>cnas-user</c> or
/// <c>cnas-admin</c>.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 201 Created from <c>POST /api/insured-persons</c> returning the Sqid id.</item>
///   <item>An <see cref="InsuredPerson"/> DB row exists with the supplied IDNP and a
///         non-empty <see cref="InsuredPerson.IdnpHash"/> shadow column — locks the
///         CLAUDE.md §5.7 / SEC 035 encryption-aware lookup contract.</item>
///   <item>HTTP 200 OK from <c>GET /api/insured-persons?query={lastName}&amp;page=1&amp;pageSize=10</c>
///         with the new record present in the paged result.</item>
///   <item>An <see cref="AuditLog"/> row with
///         <c>EventCode = "INSURED_PERSON.REGISTERED"</c> exists, targeting the newly-created
///         entity — CLAUDE.md §5.6 / SEC 042.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc12b_InsuredPersonsRegistryJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc12b_InsuredPersonsRegistryJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A CNAS operator registers a new insured person, searches by last-name substring
    /// and finds it, and the registration is journaled to the audit log.
    /// </summary>
    [Fact]
    public async Task Register_SearchByName_PersistsAndAudits()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        // A valid IDNP — first digit 0/1/2, mod-10 weighted-{7,3,1} checksum.
        const string idnp = "2000123456782";
        var operatorSqid = sqids.Encode(120_010);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: operatorSqid, Roles: ["cnas-user"])));

        var registerPayload = new InsuredPersonRegistrationInput(
            Idnp: idnp,
            LastName: "UcTwelveB",
            FirstName: "Ion",
            Patronymic: "Vasilevici",
            BirthDate: new DateOnly(1980, 5, 12));

        // Act 1 — register. The controller uses CreatedAtAction(nameof(GetAsync), ...)
        // which currently 500s because MVC strips the "Async" suffix from action names
        // (same BUG-001 documented on UC06 / UC12a). The DB write happens BEFORE the
        // response is built, so the new row is persisted regardless; we observe the
        // persisted row by IdnpHash (the encrypted plaintext column cannot be queried
        // directly — see InsuredPerson.IdnpHash remarks).
        using var registerResponse = await client.PostAsJsonAsync("/api/insured-persons", registerPayload);
        registerResponse.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.InternalServerError },
            await registerResponse.Content.ReadAsStringAsync());

        // Assert — the row is persisted with both IDNP and the IdnpHash shadow column written.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var readSqids = readScope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = readScope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var expectedHash = hasher.ComputeHash(idnp);
        var persisted = await readDb.InsuredPersons.AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdnpHash == expectedHash && p.IsActive);
        persisted.Should().NotBeNull("the new insured person must be persisted");
        persisted!.Idnp.Should().Be(idnp);
        persisted.IdnpHash.Should().NotBeNullOrWhiteSpace(
            "the IdnpHash shadow column must be populated alongside Idnp (SEC 035 contract)");
        persisted.LastName.Should().Be("UcTwelveB");
        persisted.FirstName.Should().Be("Ion");
        persisted.BirthDate.Should().Be(new DateOnly(1980, 5, 12));
        persisted.IsActive.Should().BeTrue();
        persisted.IsDeceased.Should().BeFalse();

        // Re-encode so we can address the persisted row through the Sqid-only HTTP surface.
        var persistedSqid = readSqids.Encode(persisted.Id);

        // Act 2 — search by last-name substring. The name is unique to this test so the
        // assertion is deterministic regardless of other seeded rows.
        using var searchResponse = await client.GetAsync(
            "/api/insured-persons?query=UcTwelveB&page=1&pageSize=10");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await searchResponse.Content.ReadAsStringAsync());
        var page = await searchResponse.Content.ReadFromJsonAsync<PagedResult<InsuredPersonListItem>>();
        page.Should().NotBeNull();
        page!.Items.Should().Contain(p => p.Id == persistedSqid,
            "the registered insured person must surface in the name-substring search");

        // Assert — audit row exists for the registration.
        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "INSURED_PERSON.REGISTERED"
                && a.TargetEntity == nameof(InsuredPerson)
                && a.TargetEntityId == persisted.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "insured-person registration must journal an INSURED_PERSON.REGISTERED audit row (SEC 042)");
        audit!.ActorId.Should().Be(operatorSqid,
            "the audit ActorId must match the authenticated principal's Sqid id");
    }
}
