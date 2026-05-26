using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC13 — "Gestionarea profilului solicitantului" (self-service profile management).
/// End-to-end journey covering the two-endpoint loop a Solicitant goes through to read
/// and update their own contact + i18n preferences. Drives the real
/// <c>ProfileController</c> + <c>ProfileService</c> + EF Core stack through HTTP so the
/// <see cref="Cnas.Ps.Api.Composition.HttpCallerContext"/> claim resolution, the Sqid
/// round-trip, the persistence write, and the post-write read are all exercised at the
/// HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b>
/// <list type="bullet">
///   <item>Solicitant — authenticated via <see cref="TestAuthHandler"/> with the
///         <c>cnas-user</c> role. The endpoints are plain <c>[Authorize]</c> (no role
///         requirement) so any authenticated principal would suffice; we use
///         <c>cnas-user</c> to stay aligned with the rest of the journey suite.</item>
///   <item>Anonymous — no <see cref="TestAuthHandler.HeaderName"/> header. The pipeline
///         emits 401 because the controller carries <c>[Authorize]</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/profile/me</c> with a <see cref="ProfileOutput"/>
///         whose <see cref="ProfileOutput.Id"/> equals the authenticated principal's Sqid.</item>
///   <item>HTTP 204 No Content from <c>PUT /api/profile/me</c> with a small
///         <see cref="ProfileUpdateInput"/> (the controller returns
///         <see cref="Microsoft.AspNetCore.Mvc.NoContentResult"/> per its XML doc).</item>
///   <item>A follow-up <c>GET /api/profile/me</c> returns the updated <c>Email</c> and
///         <c>PreferredLanguage</c> — the persistence round-trip survives a fresh
///         request and a fresh DbContext scope.</item>
///   <item>Anonymous <c>GET /api/profile/me</c> returns HTTP 401 Unauthorized.</item>
/// </list>
/// </para>
/// <para>
/// <b>Phone persistence (batch lands).</b> The phone-persistence follow-up adds a
/// <see cref="UserProfile.PhoneE164"/> column (encrypted at rest per CLAUDE.md §5.7 /
/// TOR SEC 035) which the <see cref="Cnas.Ps.Infrastructure.Services.ProfileService"/>
/// projects on <c>GET</c> and writes on <c>PUT</c>. The
/// <see cref="UpdateThenGet_RoundTripsUpdatedValuesAcrossRequests"/> test below therefore
/// asserts the round-trip of the Phone field too — the TODO that was here previously is
/// now a load-bearing equality assertion against <c>+37369123456</c>.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc13_ProfileSelfServiceJourneyTests
{
    /// <summary>Shared authenticated host fixture (xUnit collection-scoped).</summary>
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc13_ProfileSelfServiceJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A Solicitant reads their own profile and the response carries the seeded display
    /// name, email, and language preference together with the Sqid id derived from the
    /// authenticated principal. Locks the wiring between the claim pipeline, the caller
    /// context, the Sqid round-trip, and the EF lookup.
    /// </summary>
    [Fact]
    public async Task GetMine_SeededSolicitant_Returns200WithMatchingProfile()
    {
        // Arrange — seed a UserProfile row, then authenticate as that user via the Sqid
        // of the seeded row.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var profile = new UserProfile
        {
            MPassSubject = "uc13-sub-get",
            DisplayName = "UC13 Get Solicitant",
            Email = "uc13-get@example.test",
            PreferredLanguage = "ro",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        var callerSqid = sqids.Encode(profile.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(
                    Sub: callerSqid,
                    Roles: ["cnas-user"],
                    Idnp: "2000000000007")));

        // Act
        using var response = await client.GetAsync("/api/profile/me");

        // Assert — 200 with the persisted profile, addressed by the same Sqid the caller
        // is authenticated as (RULE 3 round-trip).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ProfileOutput>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(callerSqid,
            "the profile Sqid must match the Sqid the caller is authenticated as");
        body.DisplayName.Should().Be("UC13 Get Solicitant");
        body.Email.Should().Be("uc13-get@example.test");
        body.PreferredLanguage.Should().Be("ro");
    }

    /// <summary>
    /// A Solicitant pushes a profile update via PUT and the response is 204 No Content.
    /// The controller's XML doc commits to 204 on success — locking the contract here
    /// prevents an accidental drift to 200 + body, which would break the front-end's
    /// optimistic-update path that doesn't read the response body.
    /// </summary>
    [Fact]
    public async Task UpdateMine_ValidPayload_Returns204AndPersists()
    {
        // Arrange — seed a UserProfile row to mutate.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var profile = new UserProfile
        {
            MPassSubject = "uc13-sub-update",
            DisplayName = "UC13 Update Solicitant",
            Email = "uc13-update-before@example.test",
            PreferredLanguage = "ro",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        var callerSqid = sqids.Encode(profile.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: callerSqid, Roles: ["cnas-user"])));

        // The Phone value rides the encrypted PhoneE164 column. This test focuses on the
        // 204 contract + non-Phone field persistence; the Phone round-trip is exercised in
        // UpdateThenGet_RoundTripsUpdatedValuesAcrossRequests.
        var update = new ProfileUpdateInput(
            Email: "new@example.md",
            Phone: "+37369000000",
            PreferredLanguage: "ru");

        // Act
        using var response = await client.PutAsJsonAsync("/api/profile/me", update);

        // Assert — 204 No Content per the controller XML doc.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        // Assert — the row is mutated on disk. Read via a fresh scope so the post-commit
        // state is observed (the same pattern used by UC12a).
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == profile.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Email.Should().Be("new@example.md",
            "the update must persist the new email");
        refreshed.PreferredLanguage.Should().Be("ru",
            "the update must persist the new language preference");
    }

    /// <summary>
    /// After a successful PUT, a follow-up GET on the same authenticated principal returns
    /// the updated values — the persistence round-trip survives a fresh HTTP request and
    /// a fresh DbContext scope.
    /// </summary>
    [Fact]
    public async Task UpdateThenGet_RoundTripsUpdatedValuesAcrossRequests()
    {
        // Arrange — seed a UserProfile and capture its Sqid.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var profile = new UserProfile
        {
            MPassSubject = "uc13-sub-round",
            DisplayName = "UC13 Round-Trip Solicitant",
            Email = "uc13-round-before@example.test",
            PreferredLanguage = "ro",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        var callerSqid = sqids.Encode(profile.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: callerSqid, Roles: ["cnas-user"])));

        var update = new ProfileUpdateInput(
            Email: "uc13-round-after@example.test",
            Phone: "+37369123456",
            PreferredLanguage: "en");

        // Act 1 — PUT the update.
        using var putResponse = await client.PutAsJsonAsync("/api/profile/me", update);
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await putResponse.Content.ReadAsStringAsync());

        // Act 2 — GET the profile again on the same authenticated principal.
        using var getResponse = await client.GetAsync("/api/profile/me");

        // Assert — the GET reflects the values pushed by the PUT.
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await getResponse.Content.ReadAsStringAsync());
        var body = await getResponse.Content.ReadFromJsonAsync<ProfileOutput>();
        body.Should().NotBeNull();
        body!.Email.Should().Be("uc13-round-after@example.test",
            "the email update must survive a fresh GET on the same principal");
        body.PreferredLanguage.Should().Be("en",
            "the language update must survive a fresh GET on the same principal");
        body.Phone.Should().Be("+37369123456",
            "the phone update must survive a fresh GET on the same principal — the value is "
            + "encrypted at rest via EncryptedStringConverter and decrypted transparently on read.");
    }

    /// <summary>
    /// Anonymous callers receive 401. Sending the request without the
    /// <see cref="TestAuthHandler.HeaderName"/> header causes the test-auth handler to
    /// return <see cref="Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult"/>;
    /// the controller's <c>[Authorize]</c> attribute then escalates to a 401 challenge.
    /// </summary>
    [Fact]
    public async Task GetMine_Anonymous_Returns401()
    {
        // Arrange — deliberately omit the X-Test-User header.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };

        // Act
        using var response = await client.GetAsync("/api/profile/me");

        // Assert — 401 Unauthorized; the ProfileService never runs.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            await response.Content.ReadAsStringAsync());
    }
}
