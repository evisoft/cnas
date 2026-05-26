using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC15 — "Configurez serviciu electronic" (configure electronic service). The functional
/// administrator (persona <c>cnas-admin</c>) creates, updates, and lists
/// <c>ServicePassport</c> definitions — the JSON-schema-driven record that links a public
/// service to its workflow code, form schema, and metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active — endpoint landed in this batch.</b> The
/// <see cref="Cnas.Ps.Api.Controllers.ServicePassportsController"/> is wired over
/// <see cref="Cnas.Ps.Application.UseCases.IServicePassportService"/> with the
/// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasAdmin"/> policy gate.
/// The journey below exercises the create → list → get round-trip plus the policy gate.
/// </para>
/// <para>
/// <b>Persona.</b> Functional administrator (<c>cnas-admin</c>). Service-passport edits
/// are user-administrative actions (catalogue management) rather than infrastructure
/// operations, so they belong under <c>CnasAdmin</c> rather than
/// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasTechAdmin"/>.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc15_ServicePassportAdminJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc15_ServicePassportAdminJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An admin creates a service passport via <c>POST /api/service-passports</c>, the new
    /// passport appears in the listing, and a follow-up GET returns the full detail. Locks
    /// the wiring between the controller policy gate, the Sqid round-trip, and the EF
    /// persistence layer.
    /// </summary>
    [Fact]
    public async Task CreateAndList_AdminPersona_PersistsAndRoundTrips()
    {
        // Arrange — admin persona; no seeding needed (the endpoint creates the passport).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(150_001), Roles: ["cnas-admin"])));

        // Unique code per test run so reruns in the same process don't trip a unique-key conflict
        // on the ServicePassports.Code column. The Guid suffix is cheap and irreversible.
        var passportCode = $"SP-UC15-{Guid.NewGuid():N}".Substring(0, 16);

        var input = new ServicePassportInput(
            Id: null,
            Code: passportCode,
            NameRo: "Serviciu UC15 E2E",
            NameEn: "UC15 E2E service",
            NameRu: "Сервис UC15",
            DescriptionRo: "Pașaport seed pentru jurnalul UC15.",
            FormSchemaJson: "{\"type\":\"object\"}",
            WorkflowCode: "wf-uc15-e2e",
            MaxProcessingDays: 30,
            IsEnabled: true,
            IsProactive: false,
            DecisionRulesJson: "{}");

        // Act 1 — POST /api/service-passports creates the row.
        using var createResponse = await client.PostAsJsonAsync("/api/service-passports", input);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            await createResponse.Content.ReadAsStringAsync());

        // The controller returns a bare string Sqid as the body. ASP.NET may render it as
        // text/plain rather than JSON; reading the raw response text + trimming any quote
        // characters covers both branches.
        var newSqidRaw = await createResponse.Content.ReadAsStringAsync();
        var newSqid = newSqidRaw.Trim('"');
        newSqid.Should().NotBeNullOrWhiteSpace();

        // Act 2 — GET /api/service-passports lists active passports and the new one appears.
        using var listResponse = await client.GetAsync("/api/service-passports");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await listResponse.Content.ReadAsStringAsync());

        var list = await listResponse.Content.ReadFromJsonAsync<List<ServicePassportListItem>>();
        list.Should().NotBeNull();
        list!.Should().Contain(p => p.Id == newSqid && p.Code == passportCode,
            "the newly-created passport must surface in the active listing");

        // Act 3 — GET /api/service-passports/{sqid} returns the full detail.
        using var getResponse = await client.GetAsync($"/api/service-passports/{newSqid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await getResponse.Content.ReadAsStringAsync());

        var detail = await getResponse.Content.ReadFromJsonAsync<ServicePassportDetailOutput>();
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(newSqid, "the Sqid round-trip must preserve the external identifier (RULE 3)");
        detail.Code.Should().Be(passportCode);
        detail.NameRo.Should().Be("Serviciu UC15 E2E");
        detail.WorkflowCode.Should().Be("wf-uc15-e2e");
        detail.IsEnabled.Should().BeTrue();

        // Assert — the underlying DB row exists and carries the upserted fields.
        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var verifySqids = verifyScope.ServiceProvider.GetRequiredService<ISqidService>();
        var decoded = verifySqids.TryDecode(newSqid);
        decoded.IsSuccess.Should().BeTrue();
        var persisted = await verifyDb.ServicePassports.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == decoded.Value);
        persisted.Should().NotBeNull("the passport must be persisted by the service layer");
        persisted!.Code.Should().Be(passportCode);
        persisted.WorkflowCode.Should().Be("wf-uc15-e2e");
    }

    /// <summary>
    /// A <c>cnas-user</c> persona is rejected by the controller's CnasAdmin policy gate
    /// when attempting to create a service passport — defense in depth for the policy.
    /// </summary>
    [Fact]
    public async Task CreatePassport_UserPersona_Returns403()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(150_002), Roles: ["cnas-user"])));

        var input = new ServicePassportInput(
            Id: null,
            Code: $"SP-UC15F-{Guid.NewGuid():N}".Substring(0, 16),
            NameRo: "Serviciu forbidden",
            NameEn: null,
            NameRu: null,
            DescriptionRo: "Forbidden",
            FormSchemaJson: "{}",
            WorkflowCode: "wf-uc15-forbidden",
            MaxProcessingDays: 30,
            IsEnabled: true,
            IsProactive: false,
            DecisionRulesJson: "{}");

        // Act
        using var response = await client.PostAsJsonAsync("/api/service-passports", input);

        // Assert — the CnasAdmin policy rejects cnas-user with 403.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }
}
