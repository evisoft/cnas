using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Archive;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Archive;

/// <summary>
/// R0332 / TOR CF 12.02 — bUnit tests for the electronic-archive tabbed UI
/// at <c>/archive</c>. Verifies:
/// <list type="bullet">
///   <item>the five tabs render (Contributors / Insured Persons / Decisions /
///         Dossiers / Documents);</item>
///   <item>the metadata chip strip resolves from <c>/api/archive/summary</c>;</item>
///   <item>switching tabs triggers a new tab-list API call (lazy load);</item>
///   <item>anonymous callers see the sign-in prompt and do NOT hit the API.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 the assertions target stable <c>data-testid</c>
/// hooks: <c>archive-heading</c>, <c>archive-tab</c>, <c>archive-chips</c>,
/// <c>archive-chip-active</c>, <c>archive-chip-archived</c>,
/// <c>archive-tab-panel</c>, <c>archive-anonymous</c>. Every assertion that
/// depends on an awaited HTTP call is wrapped in <c>WaitForAssertion</c>.
/// </remarks>
public sealed class ArchiveTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires bUnit's service container with the minimum services the archive
    /// page expects: a mocked HTTP client, the API client wrapper, the
    /// localiser, and a loose JS runtime so any layout-level interop calls
    /// don't bring the render down.
    /// </summary>
    public ArchiveTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Canonical authenticated CNAS-staff session.</summary>
    /// <returns>An authenticated session.</returns>
    private static UserSession AuthenticatedSession()
        => new(true, new ProfileOutput("u1", "Examinator", "ex@cnas.md", null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    /// <summary>Sample summary used by the tests.</summary>
    private static ArchiveSummaryDto SampleSummary() =>
        new(
            Contributors: new ArchiveTabSummaryDto("contributors", 5, 1, new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc)),
            InsuredPersons: new ArchiveTabSummaryDto("insured-persons", 10, 0, new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc)),
            Decisions: new ArchiveTabSummaryDto("decisions", 2, 0, null),
            Dossiers: new ArchiveTabSummaryDto("dossiers", 3, 1, null),
            Documents: new ArchiveTabSummaryDto("documents", 7, 2, null));

    /// <summary>
    /// Happy path — the page renders the heading, the five tab buttons, and
    /// the metadata chip strip sourced from the <c>/api/archive/summary</c>
    /// payload. The chip values come straight from the summary, so a
    /// well-known active count is asserted to lock the binding.
    /// </summary>
    [Fact]
    public void Archive_WhenAuthenticated_RendersFiveTabsAndChips()
    {
        var summary = SampleSummary();
        _mock.When("https://api.test/api/archive/summary")
            .Respond("application/json", JsonSerializer.Serialize(summary));
        _mock.When("https://api.test/api/contributors*")
            .Respond("application/json", JsonSerializer.Serialize(
                new PagedResult<ContributorListItem>(Array.Empty<ContributorListItem>(), 1, 20, 0)));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Archive.Archive>(p => p.Add(c => c.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='archive-heading']").Should().NotBeNull();
            var tabs = cut.FindAll("[data-testid='archive-tab']");
            tabs.Count.Should().Be(5, "the archive page lists the five register tabs");
            cut.Find("[data-testid='archive-chips']").Should().NotBeNull();
            cut.Find("[data-testid='archive-chip-active']").TextContent.Should().Contain("5",
                "the contributors-tab active chip resolves from the summary's TotalActive");
        });
    }

    /// <summary>
    /// Tab-switch — clicking the insured-persons tab triggers the
    /// <c>/api/insured-persons</c> call (lazy load); after the click the
    /// page's <c>data-active-tab</c> attribute reflects the new tab.
    /// </summary>
    [Fact]
    public void Archive_WhenSwitchingTab_LoadsTargetTabList()
    {
        _mock.When("https://api.test/api/archive/summary")
            .Respond("application/json", JsonSerializer.Serialize(SampleSummary()));
        _mock.When("https://api.test/api/contributors*")
            .Respond("application/json", JsonSerializer.Serialize(
                new PagedResult<ContributorListItem>(Array.Empty<ContributorListItem>(), 1, 20, 0)));
        var insuredStub = _mock.When("https://api.test/api/insured-persons*")
            .Respond("application/json", JsonSerializer.Serialize(
                new PagedResult<InsuredPersonListItem>(Array.Empty<InsuredPersonListItem>(), 1, 20, 0)));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Archive.Archive>(p => p.Add(c => c.Session, AuthenticatedSession()));

        // Wait until the page is in the loaded state before clicking — the
        // chip strip is the canonical marker.
        cut.WaitForAssertion(() => cut.Find("[data-testid='archive-chips']"));

        var insuredTabButton = cut.FindAll("[data-testid='archive-tab']")
            .First(b => b.GetAttribute("data-tab") == "insured-persons");
        insuredTabButton.Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='archive-tab-panel']")
                .GetAttribute("data-active-tab").Should().Be("insured-persons");
            _mock.GetMatchCount(insuredStub).Should().BeGreaterThanOrEqualTo(1,
                "the insured-persons tab triggers a search call on activation");
        });
    }

    /// <summary>
    /// Auth gate — anonymous callers see the sign-in prompt; the
    /// <c>/api/archive/summary</c> endpoint is NOT hit so we never leak the
    /// authenticated surface to unauth callers.
    /// </summary>
    [Fact]
    public void Archive_WhenUnauthenticated_ShowsSignInPromptAndDoesNotCallApi()
    {
        var summaryStub = _mock.When("https://api.test/api/archive/summary")
            .Respond("application/json", JsonSerializer.Serialize(SampleSummary()));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Archive.Archive>();

        cut.Find("[data-testid='archive-anonymous']").Should().NotBeNull();
        _mock.GetMatchCount(summaryStub).Should().Be(0,
            "the page must not call the API on the anonymous branch");
    }
}
