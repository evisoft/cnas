using System.Net;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages;
using Cnas.Ps.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>
/// R0381 / UC05 — bUnit tests for the supervisor workspace
/// <see cref="SupervisorWorkspace"/> page. Verifies the table renders with a
/// row per task, the empty-state, the error alert when
/// <c>/api/tasks/supervisor/team</c> fails, and the heading remains visible.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 the assertions target stable <c>data-testid</c> hooks
/// the page exposes: <c>heading</c>, <c>team-queue-table</c>,
/// <c>team-task-row</c>, <c>empty-queue</c>, <c>error-alert</c>,
/// <c>reassign-button</c>. Awaited state changes are wrapped in
/// <c>WaitForAssertion</c> to avoid the parallel-test re-render race
/// documented in <c>DashboardTests</c> (#80).
/// </remarks>
public sealed class SupervisorWorkspaceTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires bUnit's service container with the minimal services every Web page
    /// expects: a mocked <see cref="HttpClient"/>, the API client wrapper, the
    /// localiser, and a loose JS runtime so any layout-level interop calls
    /// don't bring the render down.
    /// </summary>
    public SupervisorWorkspaceTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Builds the canonical authenticated supervisor session used by every
    /// test that wants to exercise the API-calling branch of the page.
    /// </summary>
    /// <returns>An authenticated session with a stub supervisor profile.</returns>
    private static UserSession AuthenticatedSession()
        => new(true, new ProfileOutput("u1", "Director Maria", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    /// <summary>
    /// Happy path — the API returns two team-task rows and the page renders one
    /// <c>[data-testid='team-task-row']</c> per item, together with the heading
    /// and the table container.
    /// </summary>
    [Fact]
    public void Supervisor_WhenApiReturnsItems_RendersTableRows()
    {
        var paged = new PagedResult<SupervisorTeamTaskDto>(
            Items: new[]
            {
                new SupervisorTeamTaskDto(
                    Id: "t1",
                    Title: "Examinare dosar A",
                    Status: "Pending",
                    DueAtUtc: new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                    DossierId: "d1",
                    AssigneeSqid: "u101",
                    AssigneeDisplayName: "Ion Popescu"),
                new SupervisorTeamTaskDto(
                    Id: "t2",
                    Title: "Examinare dosar B",
                    Status: "InProgress",
                    DueAtUtc: new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc),
                    DossierId: "d2",
                    AssigneeSqid: "u102",
                    AssigneeDisplayName: "Maria Ionescu"),
            },
            Page: 1, PageSize: 20, TotalCount: 2);

        _mock.When("https://api.test/api/tasks/supervisor/team*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var cut = RenderComponent<SupervisorWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='heading']").Should().NotBeNull();
            cut.Find("[data-testid='team-queue-table']").Should().NotBeNull();
            var rows = cut.FindAll("[data-testid='team-task-row']");
            rows.Count.Should().Be(2);
            cut.Markup.Should().Contain("Examinare dosar A");
            cut.Markup.Should().Contain("Ion Popescu");
            cut.FindAll("[data-testid='reassign-button']").Count.Should().Be(2);
        });
    }

    /// <summary>
    /// Empty-state — the API returns an empty page and the dedicated
    /// <c>[data-testid='empty-queue']</c> container renders in place of the
    /// table.
    /// </summary>
    [Fact]
    public void Supervisor_WhenEmpty_ShowsEmptyMessage()
    {
        var empty = new PagedResult<SupervisorTeamTaskDto>(
            Items: Array.Empty<SupervisorTeamTaskDto>(),
            Page: 1, PageSize: 20, TotalCount: 0);

        _mock.When("https://api.test/api/tasks/supervisor/team*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        var cut = RenderComponent<SupervisorWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='empty-queue']").Should().NotBeNull();
            cut.FindAll("[data-testid='team-task-row']").Count.Should().Be(0);
            cut.FindAll("[data-testid='team-queue-table']").Count.Should().Be(0);
        });
    }

    /// <summary>
    /// Failure path — the API returns 500 and the page renders the
    /// <c>[data-testid='error-alert']</c> container with the server-provided
    /// detail. Heading must still be visible (page chrome stays mounted).
    /// </summary>
    [Fact]
    public void Supervisor_WhenApiFails_ShowsErrorAlert()
    {
        const string problemBody = "upstream queue unavailable";
        _mock.When("https://api.test/api/tasks/supervisor/team*")
            .Respond(HttpStatusCode.InternalServerError, "text/plain", problemBody);

        var cut = RenderComponent<SupervisorWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='heading']").Should().NotBeNull();
            cut.Find("[data-testid='error-alert']").Should().NotBeNull();
            cut.Find("[data-testid='error-detail']").TextContent.Should().Contain(problemBody);
            cut.FindAll("[data-testid='team-queue-table']").Count.Should().Be(0);
        });
    }
}
