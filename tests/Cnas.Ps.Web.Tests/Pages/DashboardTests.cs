using System.Net;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>Tests for the citizen <see cref="Dashboard"/> page.</summary>
public sealed class DashboardTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public DashboardTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Dashboard_WhenAuthenticated_RendersGreetingAndKpis()
    {
        var page = new PagedResult<ApplicationListItemOutput>(
            Items: new[]
            {
                new ApplicationListItemOutput("a1", "Approved", "REF-001", "u1", DateTime.UtcNow),
                new ApplicationListItemOutput("a2", "UnderExamination", "REF-002", "u1", DateTime.UtcNow),
            },
            Page: 1, PageSize: 5, TotalCount: 2);
        _mock.When("https://api.test/api/applications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(page));
        _mock.When("https://api.test/api/dashboard/snapshot")
            .Respond("application/json", JsonSerializer.Serialize(new DashboardSnapshotDto(
                Widgets: Array.Empty<KpiWidget>(),
                KpiGrid: Array.Empty<KpiGridCellDto>())));

        var session = new UserSession(true, new ProfileOutput("u1", "Ion", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

        var cut = RenderComponent<Dashboard>(p => p.Add(d => d.Session, session));

        // Greeting element rendered (localizer may fall back to the key under bUnit since
        // the resx isn't loaded in the test assembly; the structural check is what matters).
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='greeting']").Should().NotBeNull();
            cut.Find("[data-testid='kpi-total']").TextContent.Should().Contain("2");
            cut.Find("[data-testid='kpi-underexam']").TextContent.Should().Contain("1");
            cut.Find("[data-testid='kpi-approved']").TextContent.Should().Contain("1");
        });
    }

    /// <summary>
    /// R0533 / R0534 — when the snapshot endpoint returns KPI grid cells AND widgets
    /// carrying <c>DeepLinkUrl</c>, the page renders each cell value inside an anchor
    /// (<c>&lt;a href&gt;</c>) so a click drills into the linked record list.
    /// </summary>
    [Fact]
    public void Dashboard_RendersAnchorsForKpiGridAndWidgetDeepLinks()
    {
        var page = new PagedResult<ApplicationListItemOutput>(
            Items: Array.Empty<ApplicationListItemOutput>(),
            Page: 1, PageSize: 5, TotalCount: 0);
        _mock.When("https://api.test/api/applications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(page));

        var snapshot = new DashboardSnapshotDto(
            Widgets: new[]
            {
                new KpiWidget(
                    Code: "APPROVAL_QUEUE",
                    Title: "Aprobări",
                    Value: 7,
                    Unit: "cereri",
                    Category: "ItemsAwaitingApproval",
                    DeepLinkUrl: "/approvals"),
            },
            KpiGrid: new[]
            {
                new KpiGridCellDto(
                    Code: "UNREAD_NOTIFICATIONS",
                    Title: "Necitite",
                    Value: 20,
                    Trend: null,
                    DeepLinkUrl: "/inbox"),
                new KpiGridCellDto(
                    Code: "DOCS_PENDING_APPROVAL",
                    Title: "În aprobare",
                    Value: 41,
                    Trend: null,
                    DeepLinkUrl: "/approvals"),
            });
        _mock.When("https://api.test/api/dashboard/snapshot")
            .Respond("application/json", JsonSerializer.Serialize(snapshot));

        var session = new UserSession(true, new ProfileOutput("u1", "Ion", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

        var cut = RenderComponent<Dashboard>(p => p.Add(d => d.Session, session));

        cut.WaitForAssertion(() =>
        {
            // Each KPI cell with a deep-link renders an anchor.
            cut.Find("[data-testid='kpi-link-UNREAD_NOTIFICATIONS']")
                .GetAttribute("href").Should().Be("/inbox");
            cut.Find("[data-testid='kpi-link-DOCS_PENDING_APPROVAL']")
                .GetAttribute("href").Should().Be("/approvals");
            // The widget tile with a deep-link also renders an anchor.
            cut.Find("[data-testid='widget-link-APPROVAL_QUEUE']")
                .GetAttribute("href").Should().Be("/approvals");
        });
    }

    [Fact]
    public void Dashboard_WhenApiFails_ShowsErrorAlert()
    {
        _mock.When("https://api.test/api/applications/mine*")
            .Respond(HttpStatusCode.InternalServerError);

        var session = new UserSession(true, new ProfileOutput("u1", "Ion", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

        var cut = RenderComponent<Dashboard>(p => p.Add(d => d.Session, session));

        // The error alert only renders after OnInitializedAsync awaits the failed HTTP
        // call and re-renders — wrap in WaitForAssertion to avoid racing the re-render
        // under parallel-test pressure (same pattern as the success-path test above).
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='error-alert']").Should().NotBeNull();
        });
    }
}
