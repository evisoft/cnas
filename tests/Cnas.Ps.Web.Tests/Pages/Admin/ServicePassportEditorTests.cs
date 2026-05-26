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
/// R0640 / TOR CF 15.01-15.04 — bUnit tests for the admin
/// <see cref="ServicePassportEditor"/> page. Each test stubs the
/// <c>GET /api/service-passports/{code}</c> endpoint to pin one
/// behaviour: form population, save call, and validation error rendering.
/// </summary>
public sealed class ServicePassportEditorTests : TestContext
{
    /// <summary>Substitute HTTP handler so each test can stub independent responses.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires the bUnit container with the mocked HTTP transport, the
    /// <see cref="CnasApiClient"/> facade, localisation, loose JS interop, AND
    /// a CnasAdmin-authorised principal so the page's
    /// <c>[Authorize(Policy = "CnasAdmin")]</c> gate is satisfied.
    /// </summary>
    public ServicePassportEditorTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("admin");
        auth.SetPolicies("CnasAdmin");
    }

    /// <summary>
    /// Helper — produces a canonical <see cref="ServicePassportDetailOutput"/> for
    /// the supplied code, with deterministic field values the assertions can match.
    /// </summary>
    /// <param name="code">Logical passport code to encode in the row.</param>
    /// <returns>Populated detail DTO.</returns>
    private static ServicePassportDetailOutput Detail(string code) => new(
        Id: "passportsq1",
        Code: code,
        NameRo: "Pensie pentru limita de vârstă",
        NameEn: "Old-age pension",
        NameRu: "Пенсия по возрасту",
        DescriptionRo: "Pensie standard",
        FormSchemaJson: "{}",
        WorkflowCode: "WF-AGE",
        MaxProcessingDays: 30,
        IsEnabled: true,
        IsProactive: false,
        DecisionRulesJson: "{}",
        Version: 4,
        IsCurrent: true);

    [Fact]
    public void Editor_PopulatesFormFromApi()
    {
        _mock.When("https://api.test/api/service-passports/SP-AGE")
            .Respond("application/json", JsonSerializer.Serialize(Detail("SP-AGE")));

        var cut = RenderComponent<ServicePassportEditor>(p => p.Add(c => c.Code, "SP-AGE"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='sp-input-code']")
                .GetAttribute("value")
                .Should().Be("SP-AGE");
            cut.Find("[data-testid='sp-input-name-ro']")
                .GetAttribute("value")
                .Should().Be("Pensie pentru limita de vârstă");
        });
    }

    [Fact]
    public void Editor_SaveButton_TriggersPut()
    {
        _mock.When(HttpMethod.Get, "https://api.test/api/service-passports/SP-SAVE")
            .Respond("application/json", JsonSerializer.Serialize(Detail("SP-SAVE")));

        var putHandler = _mock
            .When(HttpMethod.Put, "https://api.test/api/service-passports/SP-SAVE")
            .Respond("application/json", "\"passportsq1\"");

        var cut = RenderComponent<ServicePassportEditor>(p => p.Add(c => c.Code, "SP-SAVE"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='sp-input-name-ro']").GetAttribute("value")
                .Should().Be("Pensie pentru limita de vârstă");
        });

        cut.Find("[data-testid='sp-input-name-ro']").Change("Pensie pentru limita de vârstă (actualizat)");
        cut.Find("[data-testid='sp-save']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(putHandler).Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void Editor_SaveFailure_RendersInlineError()
    {
        _mock.When(HttpMethod.Get, "https://api.test/api/service-passports/SP-BAD")
            .Respond("application/json", JsonSerializer.Serialize(Detail("SP-BAD")));

        // Stub PUT failure so the inline error region renders.
        _mock.When(HttpMethod.Put, "https://api.test/api/service-passports/SP-BAD")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                "{\"errorCode\":\"VALIDATION_FAILED\",\"detail\":\"NameRo is required.\"}");

        var cut = RenderComponent<ServicePassportEditor>(p => p.Add(c => c.Code, "SP-BAD"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='sp-input-name-ro']").GetAttribute("value").Should().NotBeNullOrEmpty();
        });

        cut.Find("[data-testid='sp-save']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='sp-save-error']").Count.Should().Be(1);
        });
    }
}
