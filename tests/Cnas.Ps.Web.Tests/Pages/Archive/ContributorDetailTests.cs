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
/// page <see cref="ContributorDetail"/>. Verifies:
/// <list type="bullet">
///   <item>the four tabs render (Identity / Contributions / Documents /
///         Audit History) after the initial GET resolves;</item>
///   <item>switching to a non-Identity tab updates the panel marker and
///         drives the right secondary fetch;</item>
///   <item>clicking the per-tab Export button triggers an export-pipeline
///         call against the iter-125 endpoint and surfaces the success
///         confirmation.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were authored BEFORE the page's
/// production code. Assertions target stable <c>data-testid</c> hooks
/// (<c>cd-heading</c>, <c>cd-tab</c>, <c>cd-tab-panel</c>,
/// <c>cd-export</c>, <c>cd-export-ok</c>) so refactors of the CSS class
/// names don't break the suite.
/// </remarks>
public sealed class ContributorDetailTests : TestContext
{
    /// <summary>Stub HTTP handler so each test can pin independent responses.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires the bUnit container with the mocked HTTP transport, the
    /// <see cref="CnasApiClient"/> facade, loose JS interop, AND an
    /// authorised principal so the page's <c>[Authorize]</c> gate is
    /// satisfied.
    /// </summary>
    public ContributorDetailTests()
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

    /// <summary>Deterministic contributor detail used across the suite.</summary>
    private static ContributorOutput Detail(string sqid) => new(
        Id: sqid,
        Idno: "1234567890123",
        Denumire: "SRL Sample Contributor",
        CfojCode: "CFOJ-01",
        CaemCode: "01.11",
        IsInsolvent: false,
        RegisteredAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        DeregisteredAtUtc: null);

    [Fact]
    public void ContributorDetail_AfterLoad_RendersFourTabs()
    {
        _mock.When("https://api.test/api/contributors/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));

        var cut = RenderComponent<ContributorDetail>(p => p.Add(c => c.Sqid, "sample"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='cd-heading']").Should().NotBeNull();
            cut.FindAll("[data-testid='cd-tab']").Count.Should().Be(4,
                "the detail page lists Identity / Contributions / Documents / Audit History");
            // Default landing tab is Identity so the IDNO row is visible immediately.
            cut.Find("[data-testid='cd-identity-idno']").TextContent
                .Should().Contain("1234567890123");
        });
    }

    [Fact]
    public void ContributorDetail_SwitchingToContributionsTab_LoadsHistory()
    {
        _mock.When("https://api.test/api/contributors/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));
        var historyStub = _mock
            .When("https://api.test/api/contributors/sample/source-history*")
            .Respond("application/json", JsonSerializer.Serialize(
                new ContributorSourceChangeHistoryPageDto(
                    Items: new[]
                    {
                        new ContributorSourceChangeHistoryDto(
                            Id: "hist1",
                            ContributorSqid: "sample",
                            OldSourceSystem: null,
                            NewSourceSystem: "RSUD",
                            ChangedAtUtc: new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
                            ChangedByUserSqid: null,
                            Reason: null),
                    },
                    TotalCount: 1,
                    Skip: 0,
                    Take: 20)));

        var cut = RenderComponent<ContributorDetail>(p => p.Add(c => c.Sqid, "sample"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='cd-tab-strip']"));

        // Activate the Contributions tab.
        var contribTab = cut.FindAll("[data-testid='cd-tab']")
            .First(t => t.GetAttribute("data-tab") == "contributions");
        contribTab.Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='cd-tab-panel']")
                .GetAttribute("data-active-tab").Should().Be("contributions");
            _mock.GetMatchCount(historyStub).Should().BeGreaterThanOrEqualTo(1,
                "switching to Contributions must drive a source-history fetch on first activation.");
        });
    }

    [Fact]
    public void ContributorDetail_ExportButton_TriggersExportEndpointAndConfirms()
    {
        _mock.When("https://api.test/api/contributors/sample")
            .Respond("application/json", JsonSerializer.Serialize(Detail("sample")));
        var exportStub = _mock
            .When("https://api.test/api/contributors?q=sample&format=Xlsx")
            .Respond("application/octet-stream", "abc");

        var cut = RenderComponent<ContributorDetail>(p => p.Add(c => c.Sqid, "sample"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='cd-export']"));

        cut.Find("[data-testid='cd-export']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(exportStub).Should().BeGreaterThanOrEqualTo(1,
                "the per-tab export button must hit the iter-125 export endpoint.");
            cut.FindAll("[data-testid='cd-export-ok']").Count.Should().Be(1,
                "successful export surfaces the inline confirmation marker.");
        });
    }
}
