using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC07 — "Înregistrare formular". End-to-end journey covering the server-side schema
/// validation step that runs BEFORE a workflow is started. Drives the real
/// <c>FormsController.ValidateAsync</c> + <c>FormIntakeService</c> stack through HTTP so
/// the authorization gate, the Sqid round-trip, the EF Core passport lookup, and the
/// schema validator all fire on the happy path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Solicitant — a citizen authenticated via <see cref="TestAuthHandler"/>
/// with the <c>cnas-user</c> role. UC07 is an authenticated-but-anyone endpoint (the
/// controller policy is plain <c>[Authorize]</c>), so any logged-in persona is sufficient.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>POST /api/forms/validate</c> when a payload satisfies the
///         passport's schema (empty schema accepts any object payload).</item>
///   <item>HTTP 404 Not Found when the referenced passport is disabled (<c>IsEnabled=false</c>),
///         confirming the service-layer guard maps to the right HTTP status. The 404
///         response is deliberately ambiguous between "missing", "soft-deleted", and
///         "disabled" — the controller XML doc documents this anti-enumeration choice.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc07_FormValidationJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc07_FormValidationJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A citizen submits a payload that satisfies the passport's (empty) schema and the
    /// endpoint returns 200 OK. Locks the wiring between the auth pipeline, the controller,
    /// the Sqid decoder, the EF passport lookup, and the schema validator.
    /// </summary>
    [Fact]
    public async Task Validate_PayloadMatchesEmptySchema_Returns200()
    {
        // Arrange — seed a service passport with an empty schema (accepts any JSON object).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var passport = new ServicePassport
        {
            Code = "SP-UC07-OK",
            NameRo = "Serviciu E2E UC07 ok",
            DescriptionRo = "Pașaport seed pentru jurnalul UC07 — schemă goală.",
            WorkflowCode = "wf-e2e",
            FormSchemaJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var passportSqid = sqids.Encode(passport.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(700_007), Roles: ["cnas-user"])));

        var payload = new FormValidationRequest(
            ServicePassportId: passportSqid,
            FormPayloadJson: "{\"reason\":\"e2e\"}");

        // Act
        using var response = await client.PostAsJsonAsync("/api/forms/validate", payload);

        // Assert — 200 OK with empty body (the controller returns Ok() on success).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A citizen submits against a DISABLED passport and the endpoint returns 404 Not Found.
    /// Verifies the service-layer "must be enabled" guard surfaces with the right HTTP status
    /// — the deliberate ambiguity between missing/soft-deleted/disabled is what protects the
    /// catalog from enumeration attacks.
    /// </summary>
    [Fact]
    public async Task Validate_AgainstDisabledPassport_Returns404()
    {
        // Arrange — seed a DISABLED passport (still IsActive, but IsEnabled=false).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var passport = new ServicePassport
        {
            Code = "SP-UC07-DISABLED",
            NameRo = "Serviciu E2E UC07 disabled",
            DescriptionRo = "Pașaport dezactivat — nu acceptă cereri.",
            WorkflowCode = "wf-e2e",
            FormSchemaJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = false,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var passportSqid = sqids.Encode(passport.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(700_017), Roles: ["cnas-user"])));

        var payload = new FormValidationRequest(
            ServicePassportId: passportSqid,
            FormPayloadJson: "{}");

        // Act
        using var response = await client.PostAsJsonAsync("/api/forms/validate", payload);

        // Assert — 404 Not Found per the controller's MapFailure(ErrorCodes.NotFound) branch.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }
}
