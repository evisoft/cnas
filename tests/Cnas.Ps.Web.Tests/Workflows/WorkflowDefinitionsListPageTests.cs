using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Admin.WorkflowDefinitions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Workflows;

/// <summary>
/// R0121 / CF 16.02 — RED→GREEN tests for the admin workflow-definitions list
/// page. Uses bUnit + RichardSzalay.MockHttp to stub the underlying admin
/// list endpoint exposed by the API.
/// </summary>
public sealed class WorkflowDefinitionsListPageTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public WorkflowDefinitionsListPageTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void StubList(IReadOnlyList<WorkflowDefinitionListItem> items)
    {
        _mock.When("https://api.test/api/workflows*")
            .Respond("application/json", JsonSerializer.Serialize(items));
    }

    [Fact]
    public void List_RendersRowsForEachWorkflow()
    {
        StubList(
            new[]
            {
                new WorkflowDefinitionListItem("wfsq1", "WF-AGE", 3, true, DateTime.UtcNow),
                new WorkflowDefinitionListItem("wfsq2", "WF-DISAB", 1, true, DateTime.UtcNow),
            });

        var cut = RenderComponent<WorkflowDefinitionsListPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='wf-row']").Count.Should().Be(2);
        });
    }

    [Fact]
    public void List_EmptyResponse_RendersEmptyStateMessage()
    {
        StubList(Array.Empty<WorkflowDefinitionListItem>());

        var cut = RenderComponent<WorkflowDefinitionsListPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='wf-empty-state']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void List_TextFilterEntry_TriggersReload()
    {
        StubList(
            new[]
            {
                new WorkflowDefinitionListItem("wfsq1", "WF-AGE", 1, true, DateTime.UtcNow),
            });

        var cut = RenderComponent<WorkflowDefinitionsListPage>();

        // Wait for the first load to complete and at least one row to be visible
        // — otherwise the filter input may exist but the page is still in its
        // initial load state when we dispatch the change event, racing with the
        // re-render that the binding triggers.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='wf-row']").Count.Should().Be(1);
        });

        // Trigger the input change — the page must rebind without throwing.
        var filter = cut.Find("[data-testid='wf-filter-text']");
        filter.Change("WF-AGE");

        // The response is the same stub, so the row count should remain stable
        // after the bind-triggered reload completes.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='wf-row']").Count.Should().Be(1);
        });
    }
}
