using System.Net;
using System.Net.Http;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Archive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Archive;

/// <summary>
/// R0611 / TOR CF 12.02 — bUnit tests for the per-record tabbed detail
/// page <see cref="InsuredPersonDetail"/>. Verifies:
/// <list type="bullet">
///   <item>the four tabs render (Identity / Stagiu / Documents / Audit
///         History) after the initial GET resolves;</item>
///   <item>switching to a non-Identity tab updates the panel marker;</item>
///   <item>clicking the per-tab Export button triggers an export-pipeline
///         call against the iter-125 endpoint and surfaces confirmation.</item>
/// </list>
/// </summary>
public sealed class InsuredPersonDetailTests : TestContext
{
    /// <summary>Stub HTTP handler so each test can pin independent responses.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires the bUnit container with the mocked HTTP transport, the
    /// <see cref="CnasApiClient"/> facade, loose JS interop, AND an
    /// authorised principal so the page's <c>[Authorize]</c> gate is
    /// satisfied.
    /// </summary>
    public InsuredPersonDetailTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("staff");
    }

    /// <summary>Deterministic insured-person detail used across the suite.</summary>
    private static InsuredPersonOutput Detail(string sqid) => new(
        Id: sqid,
        Idnp: "2000000000001",
        LastName: "Popescu",
        FirstName: "Ion",
        Patronymic: "Vasile",
        BirthDate: new DateOnly(1980, 5, 15),
        IsDeceased: false,
        DateOfDeath: null,
        RegisteredAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LastRspSyncUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void InsuredPersonDetail_AfterLoad_RendersFourTabs()
    {
        _mock.When("https://api.test/api/insured-persons/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));

        var cut = RenderComponent<InsuredPersonDetail>(p => p.Add(c => c.Sqid, "sample"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='ipd-heading']").Should().NotBeNull();
            cut.FindAll("[data-testid='ipd-tab']").Count.Should().Be(4,
                "the detail page lists Identity / Stagiu / Documents / Audit History");
            cut.Find("[data-testid='ipd-identity-idnp']").TextContent
                .Should().Contain("2000000000001");
        });
    }

    [Fact]
    public void InsuredPersonDetail_SwitchingToStagiuTab_FlipsActivePanel()
    {
        _mock.When("https://api.test/api/insured-persons/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));

        var cut = RenderComponent<InsuredPersonDetail>(p => p.Add(c => c.Sqid, "sample"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='ipd-tab-strip']"));

        var stagiuTab = cut.FindAll("[data-testid='ipd-tab']")
            .First(t => t.GetAttribute("data-tab") == "stagiu");
        stagiuTab.Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='ipd-tab-panel']")
                .GetAttribute("data-active-tab").Should().Be("stagiu");
            // The stagiu projection is empty until the per-person feed lands —
            // we assert the deferred empty-state marker is rendered.
            cut.Find("[data-testid='ipd-stagiu-empty']").Should().NotBeNull();
        });
    }

    [Fact]
    public void InsuredPersonDetail_ExportButton_TriggersExportEndpointAndConfirms()
    {
        _mock.When("https://api.test/api/insured-persons/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));
        var exportStub = _mock
            .When("https://api.test/api/insured-persons?query=sample&format=Xlsx")
            .Respond("application/octet-stream", "abc");

        var cut = RenderComponent<InsuredPersonDetail>(p => p.Add(c => c.Sqid, "sample"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='ipd-export']"));

        cut.Find("[data-testid='ipd-export']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(exportStub).Should().BeGreaterThanOrEqualTo(1,
                "the per-tab export button must hit the iter-125 export endpoint.");
            cut.FindAll("[data-testid='ipd-export-ok']").Count.Should().Be(1,
                "successful export surfaces the inline confirmation marker.");
        });
    }
}
