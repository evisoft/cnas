using System.Net;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages;
using Cnas.Ps.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>
/// R0361 / UC13 — bUnit tests for the citizen-self-service
/// <see cref="MyProfile"/> page at <c>/profile/me</c>. Verifies the form
/// renders pre-populated from <c>GET /api/profile/me</c>, that submitting a
/// valid edit issues <c>PUT /api/profile/contact</c>, and that a server-side
/// validation failure renders the inline error region instead of swallowing
/// the response.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these assertions are written against stable
/// <c>data-testid</c> hooks the page exposes — <c>heading</c>,
/// <c>display-name-input</c>, <c>email-input</c>, <c>phone-input</c>,
/// <c>language-select</c>, <c>save-button</c>, <c>success-toast</c>,
/// <c>error-alert</c>. Awaited state changes are wrapped in
/// <c>WaitForAssertion</c> to dodge the parallel-test re-render race documented
/// in <c>DashboardTests</c> (#80).
/// </remarks>
public sealed class MyProfileTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires bUnit's service container with the minimal services every Web
    /// page expects: a mocked <see cref="HttpClient"/>, the API client
    /// wrapper, the localiser, and a loose JS runtime so any layout-level
    /// interop calls don't bring the render down.
    /// </summary>
    public MyProfileTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Builds the canonical authenticated <see cref="UserSession"/> used by
    /// every test that wants to exercise the API-calling branch of the page.
    /// </summary>
    /// <returns>An authenticated session with a stub citizen profile.</returns>
    private static UserSession AuthenticatedSession()
        => new(true, new ProfileOutput("u1", "Ion Citizen", "old@example.md", "+37322000000", "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    /// <summary>
    /// Returns the canonical profile DTO the mock <c>GET /api/profile/me</c>
    /// endpoint serves to populate the edit form.
    /// </summary>
    /// <returns>A populated <see cref="ProfileOutput"/>.</returns>
    private static ProfileOutput CannedProfile()
        => new("u1", "Ion Citizen", "old@example.md", "+37322000000", "ro", Array.Empty<IssuedDocumentSummaryDto>());

    /// <summary>
    /// Happy load — the API serves a populated profile and the form pre-fills
    /// every input with the current value. Verifies the four core inputs are
    /// present and bound.
    /// </summary>
    [Fact]
    public void MyProfile_WhenProfileLoaded_PrePopulatesEditForm()
    {
        _mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(CannedProfile()));

        var cut = RenderComponent<MyProfile>(p => p.Add(s => s.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='heading']").Should().NotBeNull();
            cut.Find("[data-testid='display-name-input']")
                .GetAttribute("value").Should().Be("Ion Citizen");
            cut.Find("[data-testid='email-input']")
                .GetAttribute("value").Should().Be("old@example.md");
            cut.Find("[data-testid='phone-input']")
                .GetAttribute("value").Should().Be("+37322000000");
            cut.Find("[data-testid='language-select']").Should().NotBeNull();
            cut.Find("[data-testid='save-button']").Should().NotBeNull();
        });
    }

    /// <summary>
    /// Happy submit — when the user edits the fields and clicks save, the page
    /// issues a single <c>PUT /api/profile/contact</c> with the edited body and
    /// renders the success toast on a 204 response.
    /// </summary>
    [Fact]
    public void MyProfile_WhenSubmitValidEdit_PutsContactAndShowsSuccess()
    {
        _mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(CannedProfile()));

        var putStub = _mock.When(HttpMethod.Put, "https://api.test/api/profile/contact")
            .Respond(HttpStatusCode.NoContent);

        var cut = RenderComponent<MyProfile>(p => p.Add(s => s.Session, AuthenticatedSession()));

        // Wait for the form to render with pre-populated values.
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='display-name-input']")
                .GetAttribute("value").Should().Be("Ion Citizen"));

        // Edit DisplayName + Email then click save.
        cut.Find("[data-testid='display-name-input']").Change("Ion Updated");
        cut.Find("[data-testid='email-input']").Change("ion.updated@example.md");
        cut.Find("[data-testid='save-button']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(putStub).Should().Be(1);
            cut.Find("[data-testid='success-toast']").Should().NotBeNull();
        });
    }

    /// <summary>
    /// Failure path — when the contact-PUT returns 400 with a server-side
    /// validation message, the page renders the
    /// <c>[data-testid='error-alert']</c> container with the propagated detail
    /// rather than silently swallowing the failure.
    /// </summary>
    [Fact]
    public void MyProfile_WhenServerRejectsSubmit_ShowsErrorAlert()
    {
        _mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(CannedProfile()));

        const string problemBody = "Email must be valid.";
        _mock.When(HttpMethod.Put, "https://api.test/api/profile/contact")
            .Respond(HttpStatusCode.BadRequest, "text/plain", problemBody);

        var cut = RenderComponent<MyProfile>(p => p.Add(s => s.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='display-name-input']").Should().NotBeNull());

        // Submit (the boundary 400 is what we want to surface — the field-level
        // client validation is intentionally permissive so the server is always
        // the source of truth).
        cut.Find("[data-testid='email-input']").Change("nope");
        cut.Find("[data-testid='save-button']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='error-alert']").Should().NotBeNull();
            cut.Find("[data-testid='error-detail']").TextContent.Should().Contain(problemBody);
            cut.FindAll("[data-testid='success-toast']").Count.Should().Be(0);
        });
    }

    /// <summary>
    /// Auth gate — when the cascaded session is anonymous the page MUST NOT
    /// call <c>/api/profile/me</c> (the call would always 401 and would leak
    /// the endpoint's existence). The page renders the sign-in prompt instead.
    /// </summary>
    [Fact]
    public void MyProfile_WhenUnauthenticated_DoesNotRequestProfile()
    {
        var stub = _mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(CannedProfile()));

        // Render with the default anonymous session — no Session parameter supplied.
        var cut = RenderComponent<MyProfile>();

        cut.Find("[data-testid='heading']").Should().NotBeNull();
        cut.Find("[data-testid='anonymous-message']").Should().NotBeNull();
        _mock.GetMatchCount(stub).Should().Be(0);
    }
}
