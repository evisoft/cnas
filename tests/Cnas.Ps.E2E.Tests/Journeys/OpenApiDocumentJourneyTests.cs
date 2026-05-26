using System.Net;
using System.Text.Json;
using Cnas.Ps.E2E.Tests.Auth;
using Microsoft.Playwright;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// End-to-end journey covering the OpenAPI schema document served by
/// <c>app.MapOpenApi()</c>. The default route in ASP.NET Core's
/// <c>Microsoft.AspNetCore.OpenApi</c> 10.x package is <c>/openapi/v1.json</c> —
/// changing that route is a public-API breaking change for every external
/// integrator generating clients from the schema, so we lock it down here.
/// </summary>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class OpenApiDocumentJourneyTests
{
    private readonly PlaywrightFixture _playwright;
    private readonly AuthenticatedApiHostFixture _api;

    /// <summary>Injects the shared fixtures supplied by the <see cref="E2ECollection"/>.</summary>
    public OpenApiDocumentJourneyTests(PlaywrightFixture playwright, AuthenticatedApiHostFixture api)
    {
        _playwright = playwright;
        _api = api;
    }

    /// <summary>
    /// Asserts that the OpenAPI v1 document is served, is valid JSON, and carries
    /// the top-level <c>"openapi"</c> declaration that schema-driven clients (Swagger
    /// UI, openapi-generator) require.
    /// </summary>
    [Fact]
    public async Task SchemaIsServed()
    {
        // Arrange — Playwright APIRequestContext is sufficient here; we don't need a
        // rendered page, just an HTTP fetch driven by the same engine as the UI tests.
        await using var ctx = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _api.BaseAddress,
            IgnoreHTTPSErrors = true,
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                [TestAuthHandler.HeaderName] = TestPersonaHeader.Serialize(
                    new TestPrincipal(Sub: "openapi-tech-admin", Roles: ["cnas-tech-admin"])),
            },
        });

        // Act
        var response = await ctx.APIRequest.GetAsync("/openapi/v1.json");

        // Assert — 200 and JSON body parses with an "openapi" version string.
        var body = await response.TextAsync();
        response.Status.Should().Be((int)HttpStatusCode.OK, body);
        body.Should().Contain("\"openapi\"", "schema must advertise its OpenAPI version");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("openapi", out var versionElement)
            .Should().BeTrue("the document must carry the top-level OpenAPI version property");
        versionElement.GetString().Should().NotBeNullOrWhiteSpace();
    }
}
