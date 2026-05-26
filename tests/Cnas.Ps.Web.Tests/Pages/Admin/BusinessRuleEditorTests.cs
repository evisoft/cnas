using System.Net;
using System.Net.Http;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Admin;

/// <summary>
/// R0141 / TOR CF 15.03 — bUnit tests for the admin
/// <see cref="BusinessRuleEditor"/> page. Each test stubs the
/// <c>GET /api/service-passports/{code}/business-rules</c> endpoint to
/// pin one behaviour: empty-state, populated-list, save-call.
/// </summary>
public sealed class BusinessRuleEditorTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Composes the test container with mocked HTTP, the API client, localization,
    /// loose JS interop, AND a CnasAdmin-authorised user so the page's
    /// <c>[Authorize(Policy = "CnasAdmin")]</c> gate passes.
    /// </summary>
    public BusinessRuleEditorTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Pin a CnasAdmin-authorised principal so the [Authorize] gate on the
        // page passes inside the bUnit harness.
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("admin");
        auth.SetPolicies("CnasAdmin");
    }

    [Fact]
    public void Editor_WhenNoRulesConfigured_RendersEmptyState()
    {
        _mock.When("https://api.test/api/service-passports/SP-X/business-rules")
            .Respond("application/json", "[]");

        var cut = RenderComponent<BusinessRuleEditor>(p => p.Add(b => b.PassportCode, "SP-X"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='rules-empty-state']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Editor_WhenRulesPresent_RendersOneRowPerRule()
    {
        var rules = new[]
        {
            new BusinessRuleDto(
                Id: "ABCDEFGH01234567",
                Name: "Reject minors",
                ApplicantType: BusinessRuleApplicantType.Natural,
                ConditionJson: """{"rule":"fact-less-than","fact":"ageYears","value":18}""",
                DecisionOutcome: BusinessRuleDecisionOutcome.Rejected,
                Notes: null),
            new BusinessRuleDto(
                Id: "ABCDEFGH01234568",
                Name: "Legal entities require review",
                ApplicantType: BusinessRuleApplicantType.Legal,
                ConditionJson: """{"rule":"fact-equals","fact":"isLegalEntity","value":true}""",
                DecisionOutcome: BusinessRuleDecisionOutcome.RequiresReview,
                Notes: "Manual examiner review."),
        };

        _mock.When("https://api.test/api/service-passports/SP-Y/business-rules")
            .Respond("application/json", JsonSerializer.Serialize(rules));

        var cut = RenderComponent<BusinessRuleEditor>(p => p.Add(b => b.PassportCode, "SP-Y"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='rules-row']").Count.Should().Be(2);
        });
    }

    [Fact]
    public void Editor_ListLoadFailure_RendersErrorAlert()
    {
        _mock.When("https://api.test/api/service-passports/SP-Z/business-rules")
            .Respond(HttpStatusCode.InternalServerError);

        var cut = RenderComponent<BusinessRuleEditor>(p => p.Add(b => b.PassportCode, "SP-Z"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='rules-error']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Editor_SaveButton_TriggersUpsertPost()
    {
        // List GET returns empty so the form is the only thing rendered.
        _mock.When(HttpMethod.Get, "https://api.test/api/service-passports/SP-W/business-rules")
            .Respond("application/json", "[]");

        var capturedRule = new BusinessRuleDto(
            Id: "AAAA1111BBBB2222",
            Name: "New rule",
            ApplicantType: BusinessRuleApplicantType.Both,
            ConditionJson: "{}",
            DecisionOutcome: BusinessRuleDecisionOutcome.RequiresReview,
            Notes: null);

        // Pin a POST handler so we can count the matches after the button click.
        var upsertHandler = _mock
            .When(HttpMethod.Post, "https://api.test/api/service-passports/SP-W/business-rules")
            .Respond("application/json", JsonSerializer.Serialize(capturedRule));

        var cut = RenderComponent<BusinessRuleEditor>(p => p.Add(b => b.PassportCode, "SP-W"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='rules-empty-state']").Count.Should().Be(1);
        });

        // Fill name + condition + click save.
        cut.Find("[data-testid='rules-name']").Change("New rule");
        cut.Find("[data-testid='rules-condition']").Change("{}");
        cut.Find("[data-testid='rules-save']").Click();

        // Verify the POST happened — the button click MUST have triggered an
        // upsert HTTP call.
        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(upsertHandler).Should().BeGreaterThan(0);
        });
    }
}
