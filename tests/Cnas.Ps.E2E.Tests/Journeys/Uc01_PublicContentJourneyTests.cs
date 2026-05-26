using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC01 — "Explorez conținut interfață publică". End-to-end journey covering the
/// anonymous public-content surface served by <c>PublicController.SearchAsync</c>
/// (<c>GET /api/public/content</c>) per TOR §3.1 / CF 01.01–01.10. Locks the contract
/// that schemas-driven public-portal clients depend on:
/// <list type="bullet">
///   <item>Anonymous access — no <c>X-Test-User</c> header, no cookie.</item>
///   <item>200 OK with a <see cref="PagedResult{T}"/> envelope.</item>
///   <item>Sqid-encoded ids per CLAUDE.md RULE 3 — no raw <c>int</c>/<c>long</c> leakage.</item>
///   <item>Substring search over <see cref="ServicePassport.NameRo"/> /
///         <see cref="ServicePassport.DescriptionRo"/>.</item>
///   <item>Per-row payload contains no PII (CF 01.09 / SEC 044).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Actor.</b> Anonymous internet user (Utilizator internet). The journey deliberately
/// uses the bare <see cref="HttpClient"/> with no authentication header so it exercises
/// the same code path a public-portal visitor follows. The endpoint sits behind the
/// <c>Anonymous</c> rate-limiting policy (5 req/min/IP per SEC 008), so we keep the
/// number of calls per test below the per-window limit.
/// </para>
/// <para>
/// <b>Fixture choice.</b> Subscribes to <see cref="AuthenticatedE2ECollection"/> rather
/// than <see cref="E2ECollection"/> because the test seeds a <see cref="ServicePassport"/>
/// row through <c>CnasDbContext</c> — the authenticated fixture provisions the
/// field-encryption + hashing keys that the EF model requires before any save can
/// succeed. The HTTP request itself sends no auth header, so the
/// <c>[AllowAnonymous]</c>-equivalent (no <c>[Authorize]</c>) routing on
/// <c>PublicController</c> is what matters; the fixture choice is purely about DB
/// seeding capability.
/// </para>
/// <para>
/// <b>Rate-limit hammer test deferred.</b> A separate test that floods the endpoint to
/// observe a 429 would race against the in-process Kestrel runtime — the 5/min window
/// resets on a wall-clock basis and a parallel xUnit run could double-spend the quota.
/// The behaviour is already locked by the in-process unit tests against
/// <c>RateLimitingComposition</c>; here we lock only the happy-path contract.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc01_PublicContentJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture supplying the running Kestrel host.</param>
    public Uc01_PublicContentJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An anonymous client lists public content and receives a paged result that includes
    /// every enabled, active <see cref="ServicePassport"/> seeded in the database. The
    /// returned ids are Sqid-encoded strings (never the raw primary key).
    /// </summary>
    [Fact]
    public async Task PublicContent_AnonymousGet_ReturnsPagedListWithSqidIds()
    {
        // Arrange — seed a unique passport so the assertion is deterministic regardless
        // of any other rows present from parallel tests in the same fixture.
        const string uniqueName = "UC01 Pasaport Public Test SRL";
        const string uniqueDescription = "Conținut public seed pentru jurnalul UC01.";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();

        var passport = new ServicePassport
        {
            Code = "SP-UC01-E2E",
            NameRo = uniqueName,
            DescriptionRo = uniqueDescription,
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        // NO TestAuthHandler.HeaderName header is set — this is the anonymous path.

        // Act — the controller defaults page=1, pageSize=20; we set pageSize=200 to be
        // sure the seeded row falls on the first (and only) page even if other tests
        // have added enabled passports.
        using var response = await client.GetAsync("/api/public/content?page=1&pageSize=200");

        // Assert — 200 OK and a parseable PagedResult envelope.
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var page = await response.Content.ReadFromJsonAsync<PagedResult<PublicContentCard>>();
        page.Should().NotBeNull("the endpoint must return a paged envelope, not a bare array");
        page!.Items.Should().NotBeEmpty(
            "at least the UC01 seeded passport must be returned");

        var card = page.Items.SingleOrDefault(c => c.Title == uniqueName);
        card.Should().NotBeNull("the seeded passport title must round-trip into the public card");
        card!.Id.Should().NotBeNullOrWhiteSpace("the card id must be a non-empty Sqid string");
        card.Id.Should().NotMatchRegex(
            "^[0-9]+$",
            "the card id must be Sqid-encoded (CLAUDE.md RULE 3), not a raw integer key");
        card.Category.Should().Be("service");
        card.Summary.Should().StartWith("Conținut public seed", "summary should mirror DescriptionRo");

        // Sanity — Title alone is the only place a NameRo appears; no PII should be present.
        // The PublicContentCard contract has no fields that could carry PII, so we lock the
        // shape rather than scanning for specific patterns.
        page.Items.Should().AllSatisfy(c =>
        {
            c.Title.Should().NotBeNullOrWhiteSpace();
            c.Id.Should().NotBeNullOrWhiteSpace();
        });
    }

    /// <summary>
    /// An anonymous client searches by a unique query token and the response narrows to
    /// rows whose <c>NameRo</c> or <c>DescriptionRo</c> contains the token (case-insensitive
    /// substring) — the full-text behaviour described by CF 01.04 / CF 01.13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this test locks.</b> Seeds two enabled, active <see cref="ServicePassport"/>
    /// rows through <c>CnasDbContext</c>: a "match" row whose <c>NameRo</c> embeds a unique
    /// random token, and a "noise" row that does not. Hits
    /// <c>GET /api/public/content?query=&lt;token&gt;</c> anonymously (no <c>X-Test-User</c>
    /// header) and asserts:
    /// </para>
    /// <list type="bullet">
    ///   <item>200 OK with a <see cref="PagedResult{T}"/> envelope.</item>
    ///   <item>Exactly one item is returned — the seeded matching passport.</item>
    ///   <item>The noise passport is excluded.</item>
    ///   <item>The returned <c>Id</c> is a Sqid-encoded string, not a raw integer
    ///         (CLAUDE.md RULE 3).</item>
    /// </list>
    /// <para>
    /// <b>What this test locks structurally.</b> The end-to-end pass through the
    /// provider-aware seam in <c>PublicContentService.SearchAsync</c>. The fixture wires
    /// the EF Core <c>InMemory</c> provider, which cannot translate
    /// <c>EF.Functions.ILike</c>; if the seam is removed or regresses, this assertion
    /// flips to a 500 with <c>InvalidOperationException</c> — the BUG-007 signature.
    /// In production the same call path takes the <c>ILike</c> branch, which is covered
    /// by the Infrastructure-tier integration tests running against real Postgres.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PublicContent_AnonymousSearch_FiltersByQuery()
    {
        // Arrange — seed two passports: one whose NameRo embeds a unique random token,
        // and one with a deliberately disjoint NameRo/DescriptionRo. A GUID-derived token
        // keeps the assertion deterministic regardless of parallel seeds from other tests.
        var token = $"UC01TKN{Guid.NewGuid():N}".Substring(0, 18);
        var matchName = $"UC01 Pasaport căutare {token} SRL";
        const string noiseName = "UC01 Pasaport căutare ZGOMOT NoMatch SRL";
        const string matchDescription = "Conținut public seed pentru jurnalul UC01 — full-text.";
        const string noiseDescription = "Descriere de control care nu trebuie returnată.";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();

        var matchPassport = new ServicePassport
        {
            Code = $"SP-UC01-MATCH-{Guid.NewGuid():N}".Substring(0, 24),
            NameRo = matchName,
            DescriptionRo = matchDescription,
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        var noisePassport = new ServicePassport
        {
            Code = $"SP-UC01-NOISE-{Guid.NewGuid():N}".Substring(0, 24),
            NameRo = noiseName,
            DescriptionRo = noiseDescription,
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(matchPassport);
        db.ServicePassports.Add(noisePassport);
        await db.SaveChangesAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        // NO TestAuthHandler.HeaderName header is set — this is the anonymous path.

        // Act — query by the unique token. pageSize is generous so the assertion is
        // about the filter, not pagination.
        using var response = await client.GetAsync(
            $"/api/public/content?query={Uri.EscapeDataString(token)}&page=1&pageSize=200");

        // Assert — 200 OK and a parseable PagedResult envelope.
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var page = await response.Content.ReadFromJsonAsync<PagedResult<PublicContentCard>>();
        page.Should().NotBeNull("the endpoint must return a paged envelope, not a bare array");
        page!.Items.Should().HaveCount(
            1,
            "the unique token must match exactly the seeded passport and nothing else");

        var card = page.Items.Single();
        card.Title.Should().Be(matchName, "the matching passport title must round-trip");
        card.Id.Should().NotBeNullOrWhiteSpace("the card id must be a non-empty Sqid string");
        card.Id.Should().NotMatchRegex(
            "^[0-9]+$",
            "the card id must be Sqid-encoded (CLAUDE.md RULE 3), not a raw integer key");

        // And the noise row is excluded — defensive sanity check.
        page.Items.Should().NotContain(
            c => c.Title == noiseName,
            "the non-matching passport must not appear when filtered by the unique token");
    }
}
