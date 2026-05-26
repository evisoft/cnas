using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.E2E.Tests.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC09 — "Extragerea rapoartelor" (catalog discovery half). End-to-end journey covering
/// the read-only <c>GET /api/reports</c> endpoint that the front-end calls to populate the
/// report-picker drop-down. Drives the real <c>ReportsController</c> +
/// <see cref="IReportingService"/> stack through HTTP so the authorization gate
/// (<see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasUser"/>), the
/// rate-limiter partition, and the in-memory catalog snapshot all fire on the happy path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b>
/// <list type="bullet">
///   <item>cnas-decider — authenticated CNAS staff with the <c>cnas-decider</c> role.
///         The catalog endpoint accepts any authenticated CNAS staff role
///         (<c>cnas-user</c>, <c>cnas-decider</c>, <c>cnas-admin</c>) per
///         <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasUser"/>; we
///         pick decider as the requirement-mandated persona for this journey class so
///         the gate is exercised with a typical operator role.</item>
///   <item>Anonymous — no <see cref="TestAuthHandler.HeaderName"/> header attached. The
///         test-auth handler returns <see cref="Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult"/>
///         so <c>[Authorize]</c> challenges and the pipeline emits 401.</item>
/// </list>
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/reports</c> when called by an authenticated staff
///         persona.</item>
///   <item>The response body is a non-empty list of <see cref="ReportCatalogEntryOutput"/>.</item>
///   <item>The catalog includes the stable reference code
///         <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c> (one of the Annex 6j fixtures whose
///         contract is already locked by <c>RptDossiersClosedByOutcomeTests</c>).</item>
///   <item>Every entry carries a non-empty <see cref="ReportCatalogEntryOutput.Code"/> and
///         at least one non-empty title across RO/RU/EN so the front-end can always
///         render a row without a fallback to "(empty)".</item>
///   <item>HTTP 401 Unauthorized when called without the <see cref="TestAuthHandler.HeaderName"/>
///         header — anonymous access is forbidden at the controller-attribute level.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc09_ReportsCatalogJourneyTests
{
    /// <summary>Shared authenticated host fixture (xUnit collection-scoped).</summary>
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc09_ReportsCatalogJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A cnas-decider lists the report catalog and the response carries every Annex 6
    /// fixture the front-end depends on. Locks the wiring between the
    /// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasUser"/> policy
    /// gate, the controller, and the in-memory catalog snapshot.
    /// </summary>
    [Fact]
    public async Task ListAvailable_DeciderPersona_Returns200WithCatalog()
    {
        // Arrange — build a cnas-decider HTTP client.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(900_009), Roles: ["cnas-decider"])));

        // Act
        using var response = await client.GetAsync("/api/reports");

        // Assert — 200 with a non-empty catalog list.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var catalog = await response.Content.ReadFromJsonAsync<IReadOnlyList<ReportCatalogEntryOutput>>();
        catalog.Should().NotBeNull("the catalog endpoint always materialises a list");
        catalog!.Should().NotBeEmpty("the catalog ships every Annex 6 report code that materialises today");
    }

    /// <summary>
    /// The catalog contains the stable reference code <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c>
    /// (Annex 6j). The code is part of the API contract — renaming it is a breaking change
    /// per CLAUDE.md §2.2, and the front-end depends on it being addressable from the
    /// drop-down.
    /// </summary>
    [Fact]
    public async Task ListAvailable_CatalogContainsAnnex6jReferenceCode()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(900_019), Roles: ["cnas-decider"])));

        // Act
        using var response = await client.GetAsync("/api/reports");

        // Assert — the Annex 6j reference code is present.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var catalog = await response.Content.ReadFromJsonAsync<IReadOnlyList<ReportCatalogEntryOutput>>();
        catalog.Should().NotBeNull();
        catalog!.Should().Contain(e => e.Code == "RPT-DOSSIERS-CLOSED-BY-OUTCOME",
            "the Annex 6j 'Dossiers closed by outcome' code is a stable contract reference");
    }

    /// <summary>
    /// Every entry has a non-empty <see cref="ReportCatalogEntryOutput.Code"/> and at
    /// least one non-empty localised title. The contract guarantees the front-end never
    /// receives an "(empty)" row — the catalog defaults the titles to the code itself for
    /// any entry that has not been translated.
    /// </summary>
    [Fact]
    public async Task ListAvailable_EveryEntryHasCodeAndAtLeastOneTitle()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(900_029), Roles: ["cnas-decider"])));

        // Act
        using var response = await client.GetAsync("/api/reports");

        // Assert — every row satisfies the shape contract.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var catalog = await response.Content.ReadFromJsonAsync<IReadOnlyList<ReportCatalogEntryOutput>>();
        catalog.Should().NotBeNull();
        catalog!.Should().OnlyContain(
            e => !string.IsNullOrWhiteSpace(e.Code)
                 && (!string.IsNullOrWhiteSpace(e.TitleRo)
                     || !string.IsNullOrWhiteSpace(e.TitleRu)
                     || !string.IsNullOrWhiteSpace(e.TitleEn)),
            "every catalog row must carry a non-empty code and at least one localised title so the picker never renders '(empty)'");
    }

    /// <summary>
    /// Anonymous callers receive 401. Sending the request without the
    /// <see cref="TestAuthHandler.HeaderName"/> header causes the test-auth handler to
    /// return <see cref="Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult"/>,
    /// which the <c>[Authorize(Policy = CnasUser)]</c> attribute escalates to a 401
    /// challenge.
    /// </summary>
    [Fact]
    public async Task ListAvailable_Anonymous_Returns401()
    {
        // Arrange — deliberately omit the X-Test-User header.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };

        // Act
        using var response = await client.GetAsync("/api/reports");

        // Assert — 401 Unauthorized. (The ASP.NET pipeline emits 401 with an empty body
        // when no scheme can produce a principal; the catalog never reaches the controller.)
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            await response.Content.ReadAsStringAsync());
    }
}
