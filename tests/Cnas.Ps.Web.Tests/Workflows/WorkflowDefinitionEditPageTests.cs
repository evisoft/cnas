using System.Net;
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
/// R0121 / CF 16.02 — RED→GREEN tests for the workflow-definition edit page.
/// Uses bUnit + RichardSzalay.MockHttp to stub the existing
/// <c>GET /api/workflows/{code}</c> + <c>PUT /api/workflows/{code}</c> endpoints.
/// The textarea round-trips the raw definition body (JSON in production); the
/// graph renderer also accepts the simpler <c>&lt;workflow&gt;</c> XML envelope
/// described in <see cref="Cnas.Ps.Web.Models.WorkflowGraphParser"/>.
/// </summary>
public sealed class WorkflowDefinitionEditPageTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public WorkflowDefinitionEditPageTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private const string MinimalXml = """
        <workflow>
          <nodes>
            <node code="START" kind="Start" />
            <node code="END" kind="End" />
          </nodes>
          <edges>
            <edge from="START" to="END" />
          </edges>
        </workflow>
        """;

    private void StubGet(string code, string body)
    {
        // Restrict the stub to GET so a subsequent PUT-only stub registered by the
        // test isn't shadowed by this method-agnostic GET handler.
        _mock.When(HttpMethod.Get, $"https://api.test/api/workflows/{code}")
            .Respond("application/xml", body);
    }

    [Fact]
    public void Edit_OnInit_LoadsBodyIntoTextarea()
    {
        StubGet("WF-AGE", MinimalXml);

        var cut = RenderComponent<WorkflowDefinitionEditPage>(p => p.Add(x => x.WorkflowCode, "WF-AGE"));

        cut.WaitForAssertion(() =>
        {
            var ta = cut.Find("textarea[data-testid='wf-xml-textarea']");
            ta.TextContent.Should().Contain("<workflow>");
        });
    }

    [Fact]
    public void Edit_SaveButton_CallsPutWithUpdatedBody()
    {
        StubGet("WF-AGE", MinimalXml);
        var putHit = _mock.When(HttpMethod.Put, "https://api.test/api/workflows/WF-AGE")
            .Respond(HttpStatusCode.NoContent);

        var cut = RenderComponent<WorkflowDefinitionEditPage>(p => p.Add(x => x.WorkflowCode, "WF-AGE"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='wf-save-btn']").Should().NotBeNull());

        cut.Find("[data-testid='wf-save-btn']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(putHit).Should().Be(1);
        });
    }

    [Fact]
    public void Edit_InvalidXmlInTextarea_DisablesSaveAndShowsParseError()
    {
        StubGet("WF-AGE", MinimalXml);

        var cut = RenderComponent<WorkflowDefinitionEditPage>(p => p.Add(x => x.WorkflowCode, "WF-AGE"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='wf-xml-textarea']").Should().NotBeNull());

        // Inject malformed XML and trigger the re-render.
        var ta = cut.Find("[data-testid='wf-xml-textarea']");
        ta.Change("<workflow><broken");
        cut.Find("[data-testid='wf-rerender-btn']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='wf-parse-error']").Count.Should().Be(1);
            var saveBtn = cut.Find("[data-testid='wf-save-btn']");
            saveBtn.HasAttribute("disabled").Should().BeTrue();
        });
    }
}
