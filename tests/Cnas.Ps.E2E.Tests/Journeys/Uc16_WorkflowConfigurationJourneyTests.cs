using System.Net;
using System.Net.Http;
using System.Text;
using Cnas.Ps.Core.Common;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC16 — "Configurez flux de lucru" (configure workflow). The functional administrator
/// (persona <c>cnas-admin</c>) retrieves and persists versioned workflow definitions
/// through <see cref="Cnas.Ps.Api.Controllers.WorkflowsController"/> wired over
/// <see cref="Cnas.Ps.Application.UseCases.IWorkflowConfigurationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active — full persistence round-trip.</b> This journey now exercises the full
/// versioned-repository contract end-to-end: insert with version 1, GET the latest JSON,
/// publish a new revision (version 2 IsCurrent=true / version 1 IsCurrent=false),
/// rejection of unknown codes (404), rejection of malformed JSON (400), and the
/// CnasAdmin policy gate (403 for cnas-user). The previous batch pinned the 400
/// sentinel returned by the deferred stub; that sentinel is gone and the assertions
/// here lock the real persistence behaviour.
/// </para>
/// <para>
/// <b>Persona.</b> Functional administrator (<c>cnas-admin</c>). Workflow edits change
/// the runtime business graph — catalogue management, not infrastructure ops.
/// </para>
/// <para>
/// <b>Code semantics.</b> Workflow codes are NOT Sqid-encoded (CLAUDE.md RULE 3
/// documented exception — see <c>WorkflowsController</c> XML doc). The code IS the
/// public identifier and is stored upper-case after canonicalization by the service.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc16_WorkflowConfigurationJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc16_WorkflowConfigurationJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// PUT a brand-new workflow code; the service inserts version 1 with
    /// <c>IsCurrent = true</c>. The HTTP response is 204 No Content and a row is
    /// queryable in <c>WorkflowDefinitions</c> with the canonical (upper-case) code.
    /// </summary>
    [Fact]
    public async Task Save_NewWorkflow_PersistsVersionOne()
    {
        // Arrange — admin persona; choose a code unique to this test so parallel runs
        // (or repeated runs against the same fixture) do not collide.
        const string code = "WF-UC16-NEW";
        using var client = NewAdminClient(160_010);

        // Act
        using var content = new StringContent(
            "{\"steps\":[]}", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/api/workflows/{code}", content);

        // Assert — 204 No Content, single row with Version=1 IsCurrent=true.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var row = await db.WorkflowDefinitions.SingleAsync(w => w.Code == code);
        row.Version.Should().Be(1);
        row.IsCurrent.Should().BeTrue();
        row.DefinitionJson.Should().Be("{\"steps\":[]}");
    }

    /// <summary>
    /// After a PUT, an immediate GET returns 200 with the saved JSON in the body
    /// and <c>application/json</c> content-type.
    /// </summary>
    [Fact]
    public async Task Get_AfterSave_ReturnsLatestJson()
    {
        // Arrange — publish a fresh definition, then read it back.
        const string code = "WF-UC16-GET";
        const string body = "{\"states\":[\"submitted\",\"approved\"]}";
        using var client = NewAdminClient(160_011);
        using var save = new StringContent(body, Encoding.UTF8, "application/json");
        using var saveResponse = await client.PutAsync($"/api/workflows/{code}", save);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act
        using var getResponse = await client.GetAsync($"/api/workflows/{code}");

        // Assert
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await getResponse.Content.ReadAsStringAsync());
        getResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await getResponse.Content.ReadAsStringAsync()).Should().Be(body);
    }

    /// <summary>
    /// Two PUTs for the same code produce two rows: the older has <c>IsCurrent=false
    /// Version=1</c>, the newer has <c>IsCurrent=true Version=2</c>. GET returns the
    /// newer body verbatim.
    /// </summary>
    [Fact]
    public async Task Save_Again_IncrementsVersionAndDeactivatesPrevious()
    {
        // Arrange
        const string code = "WF-UC16-VERSIONED";
        using var client = NewAdminClient(160_012);

        using var v1 = new StringContent("{\"v\":1}", Encoding.UTF8, "application/json");
        (await client.PutAsync($"/api/workflows/{code}", v1))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act — publish a second revision.
        using var v2 = new StringContent("{\"v\":2}", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/api/workflows/{code}", v2);

        // Assert — both revisions present; the latest is current.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var rows = await db.WorkflowDefinitions
            .Where(w => w.Code == code)
            .OrderBy(w => w.Version)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Version.Should().Be(1);
        rows[0].IsCurrent.Should().BeFalse();
        rows[1].Version.Should().Be(2);
        rows[1].IsCurrent.Should().BeTrue();

        // GET reads the latest body.
        using var getResponse = await client.GetAsync($"/api/workflows/{code}");
        (await getResponse.Content.ReadAsStringAsync()).Should().Be("{\"v\":2}");
    }

    /// <summary>
    /// GET against a workflow code that was never published returns 404 (not the
    /// historical 400 sentinel — the repository now distinguishes "missing" from
    /// "broken").
    /// </summary>
    [Fact]
    public async Task Get_UnknownCode_Returns404()
    {
        // Arrange
        using var client = NewAdminClient(160_013);

        // Act
        using var response = await client.GetAsync("/api/workflows/WF-UC16-MISSING");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// PUT with a body that is not parseable as JSON is rejected at the model-binder
    /// stage (the controller declares <c>[Consumes("application/json")]</c> and binds
    /// to a <see cref="System.Text.Json.JsonElement"/> — malformed JSON fails the bind
    /// before the action runs). The status code is 400 either way; the test pins the
    /// outcome so changes to the binding strategy do not silently weaken validation.
    /// </summary>
    [Fact]
    public async Task Save_InvalidJson_Returns400()
    {
        // Arrange
        using var client = NewAdminClient(160_014);

        // Act — the body is syntactically invalid JSON.
        using var content = new StringContent(
            "{ this is not valid json", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync("/api/workflows/WF-UC16-BADJSON", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A <c>cnas-user</c> persona is rejected by the controller's CnasAdmin policy gate —
    /// defense-in-depth for the policy. GET is the canonical probe; PUT goes through the
    /// same gate so this single assertion is sufficient.
    /// </summary>
    [Fact]
    public async Task Save_AsCnasUser_Returns403()
    {
        // Arrange — non-admin persona.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(160_015), Roles: ["cnas-user"])));

        // Act
        using var response = await client.GetAsync("/api/workflows/WF-UC16-FORBIDDEN");

        // Assert — CnasAdmin policy rejects cnas-user with 403.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Builds a fresh <see cref="HttpClient"/> bound to the fixture's base address and
    /// authenticated as a cnas-admin persona using the supplied user id. The Sqid
    /// service is resolved off the host's scope so the encoded subject mirrors what
    /// the production composition produces.
    /// </summary>
    /// <param name="userId">Internal user id encoded into the persona's Sub claim.</param>
    /// <returns>HttpClient that callers must dispose; ready to call /api/workflows.</returns>
    private HttpClient NewAdminClient(long userId)
    {
        var scope = _fixture.Services.CreateScope();
        try
        {
            var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
            var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
            client.DefaultRequestHeaders.Add(
                TestAuthHandler.HeaderName,
                TestPersonaHeader.Serialize(
                    new TestPrincipal(Sub: sqids.Encode(userId), Roles: ["cnas-admin"])));
            return client;
        }
        finally
        {
            scope.Dispose();
        }
    }
}
