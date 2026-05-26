using System.Net;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Applications;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Applications;

/// <summary>
/// bUnit tests for the citizen application submission page (UC06 — <see cref="New"/>).
/// The page is a three-step flow: (1) list service passports, (2) pick one and
/// render its <c>FormSchemaJson</c> into a dynamic form, (3) POST the assembled
/// payload to <c>/api/applications</c> and navigate to the detail page on success.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were written against the page's stable
/// <c>data-testid</c> hooks before any wiring changes — they fail-fast if the
/// markers (<c>passport-select</c>, <c>dynamic-form</c>, <c>submit-btn</c>,
/// <c>error-alert</c>) drift. Every assertion that depends on a state change
/// after an awaited HTTP call is wrapped in <c>WaitForAssertion</c> to avoid
/// the parallel-test render race we hit in <c>DashboardTests</c> (#80).
/// </remarks>
public sealed class NewTests : TestContext
{
    /// <summary>HTTP mock backing the <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Composes the test container with the same service shape every other
    /// page test uses: mocked HTTP, the API client, the localiser, and a loose
    /// JS runtime so layout-level interop doesn't crash the render.
    /// </summary>
    public NewTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Stubs the passport listing endpoint with one passport whose
    /// <c>FormSchemaJson</c> declares a single required string field named
    /// <c>amount</c>. Returned as a tuple so individual tests can assert on
    /// the passport id when wiring follow-up POST stubs.
    /// </summary>
    /// <returns>The stub passport's Sqid id.</returns>
    private string StubPassportCatalog()
    {
        const string passportId = "psp-1";
        var listing = new[]
        {
            new ServicePassportListItem(passportId, "PS-01", "Indemnizație unică", true, 1),
        };
        _mock.When("https://api.test/api/service-passports")
            .Respond("application/json", JsonSerializer.Serialize(listing));

        var schema = """
            {
              "type": "object",
              "required": ["amount"],
              "properties": {
                "amount": { "type": "string" }
              }
            }
            """;
        var detail = new ServicePassportDetailOutput(
            Id: passportId,
            Code: "PS-01",
            NameRo: "Indemnizație unică",
            NameEn: null,
            NameRu: null,
            DescriptionRo: "",
            FormSchemaJson: schema,
            WorkflowCode: "wf",
            MaxProcessingDays: 30,
            IsEnabled: true,
            IsProactive: false,
            DecisionRulesJson: "{}",
            Version: 1,
            IsCurrent: true);
        _mock.When($"https://api.test/api/service-passports/{passportId}")
            .Respond("application/json", JsonSerializer.Serialize(detail));

        return passportId;
    }

    /// <summary>
    /// After the passport list resolves, the page renders the
    /// <c>passport-select</c> dropdown populated with one option per passport.
    /// </summary>
    [Fact]
    public void New_WhenRendered_DisplaysPassportSelect()
    {
        StubPassportCatalog();

        var cut = RenderComponent<New>();

        // The select only appears after OnInitializedAsync awaits the passport
        // listing call and re-renders — wrap in WaitForAssertion.
        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("[data-testid='passport-select']");
            select.Should().NotBeNull();
            // One placeholder option + one passport option = two <option> nodes.
            cut.FindAll("[data-testid='passport-select'] option").Count.Should().Be(2);
        });
    }

    /// <summary>
    /// Selecting a passport triggers a follow-up GET for its detail and the
    /// dynamic form renders with the schema-derived fields.
    /// </summary>
    [Fact]
    public void New_WhenPassportSelected_RendersDynamicForm()
    {
        var passportId = StubPassportCatalog();

        var cut = RenderComponent<New>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='passport-select']").Should().NotBeNull());
        cut.Find("[data-testid='passport-select']").Change(passportId);

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='dynamic-form']").Should().NotBeNull();
            cut.Find("[data-testid='input-amount']").Should().NotBeNull();
            // The "required" star marker for the only required field.
            cut.Find("[data-testid='req-amount']").Should().NotBeNull();
            cut.Find("[data-testid='submit-btn']").Should().NotBeNull();
        });
    }

    /// <summary>
    /// On submit, the page POSTs <c>SubmitApplicationInput</c> to
    /// <c>/api/applications</c>; on a 200/201 with an <see cref="ApplicationOutput"/>
    /// body the page navigates to <c>/applications/{newId}</c>.
    /// </summary>
    [Fact]
    public void New_WhenSubmitted_PostsToApiAndNavigates()
    {
        var passportId = StubPassportCatalog();
        var created = new ApplicationOutput(
            Id: "app-99",
            Status: "Submitted",
            ReferenceNumber: "REF-001",
            SubmittedAtUtc: DateTime.UtcNow);
        var postStub = _mock
            .When(HttpMethod.Post, "https://api.test/api/applications")
            .Respond("application/json", JsonSerializer.Serialize(created));

        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderComponent<New>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='passport-select']").Should().NotBeNull());
        cut.Find("[data-testid='passport-select']").Change(passportId);
        cut.WaitForAssertion(() => cut.Find("[data-testid='dynamic-form']").Should().NotBeNull());

        // Provide a value for the required field then submit the form.
        cut.Find("[data-testid='input-amount']").Change("100");
        cut.Find("[data-testid='dynamic-form']").Submit();

        // Both the POST count and the navigation only update after the awaited
        // POST round-trip resolves — WaitForAssertion to dodge the same async
        // re-render race the Dashboard tests hit in #80.
        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(postStub).Should().Be(1);
            nav.Uri.Should().EndWith("/applications/app-99");
        });
    }

    /// <summary>
    /// When the POST returns a 400 the page surfaces the error alert with the
    /// mapped error code (<c>VALIDATION_FAILED</c>) and the response body in
    /// the error-detail panel.
    /// </summary>
    [Fact]
    public void New_WhenSubmitApiFails_ShowsErrorAlert()
    {
        var passportId = StubPassportCatalog();
        const string problemBody = """{"title":"amount is required"}""";
        _mock.When(HttpMethod.Post, "https://api.test/api/applications")
            .Respond(HttpStatusCode.BadRequest, "application/json", problemBody);

        var cut = RenderComponent<New>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='passport-select']").Should().NotBeNull());
        cut.Find("[data-testid='passport-select']").Change(passportId);
        cut.WaitForAssertion(() => cut.Find("[data-testid='dynamic-form']").Should().NotBeNull());

        cut.Find("[data-testid='input-amount']").Change(string.Empty);
        cut.Find("[data-testid='dynamic-form']").Submit();

        // Error alert renders only after the failed POST resolves — wrap.
        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid='error-alert']");
            alert.Should().NotBeNull();
            // ErrorCodes.ValidationFailed = "VALIDATION_FAILED" per Cnas.Ps.Core.
            alert.TextContent.Should().Contain("VALIDATION_FAILED");
            cut.Find("[data-testid='error-detail']").TextContent.Should().Contain("amount is required");
        });
    }
}
