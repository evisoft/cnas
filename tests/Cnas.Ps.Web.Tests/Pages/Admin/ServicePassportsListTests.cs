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
/// <see cref="ServicePassports"/> list page. Stubs the
/// <c>GET /api/service-passports</c> endpoint to pin the three rendering
/// branches: empty-state, populated table, and the Edit-button navigation.
/// </summary>
public sealed class ServicePassportsListTests : TestContext
{
    /// <summary>Substitute HTTP handler so each test can stub independent responses.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires the bUnit container with the mocked HTTP transport, the
    /// <see cref="CnasApiClient"/> facade, the localisation feature, loose JS
    /// interop (no JS code paths under test), AND a CnasAdmin-authorised
    /// principal so the page's <c>[Authorize(Policy = "CnasAdmin")]</c>
    /// gate is satisfied inside the harness.
    /// </summary>
    public ServicePassportsListTests()
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

    [Fact]
    public void Page_WhenNoPassports_RendersEmptyState()
    {
        _mock.When("https://api.test/api/service-passports")
            .Respond("application/json", "[]");

        var cut = RenderComponent<ServicePassports>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='sp-empty-state']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Page_WhenPassportsExist_RendersOneRowPerPassport()
    {
        var rows = new[]
        {
            new ServicePassportListItem("sp1sq", "SP-A", "Pensia A", true, 1),
            new ServicePassportListItem("sp2sq", "SP-B", "Indemnizație B", false, 3),
        };
        _mock.When("https://api.test/api/service-passports")
            .Respond("application/json", JsonSerializer.Serialize(rows));

        var cut = RenderComponent<ServicePassports>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='sp-row']").Count.Should().Be(2);
        });
    }

    [Fact]
    public void Page_EditButton_NavigatesToEditorRoute()
    {
        var rows = new[]
        {
            new ServicePassportListItem("sp1sq", "SP-A", "Pensia A", true, 1),
        };
        _mock.When("https://api.test/api/service-passports")
            .Respond("application/json", JsonSerializer.Serialize(rows));

        var nav = Services.GetRequiredService<FakeNavigationManager>();

        var cut = RenderComponent<ServicePassports>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='sp-edit']").Count.Should().Be(1);
        });

        cut.Find("[data-testid='sp-edit']").Click();

        nav.Uri.Should().EndWith("/admin/service-passports/SP-A");
    }
}
