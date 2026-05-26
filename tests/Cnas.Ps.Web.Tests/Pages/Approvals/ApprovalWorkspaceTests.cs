using System.Net;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Approvals;
using Cnas.Ps.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Approvals;

/// <summary>
/// R0590 / TOR CF 10.01 — bUnit tests for the decider's approval workspace page
/// (<see cref="ApprovalWorkspace"/>). Mirrors the SupervisorWorkspaceTests style:
/// stable data-testid hooks, MockHttp transport, FluentAssertions, all assertions
/// wrapped in <c>WaitForAssertion</c> to ride out the bUnit re-render race.
/// </summary>
public sealed class ApprovalWorkspaceTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires bUnit's service container with the minimal services every Web
    /// page expects.
    /// </summary>
    public ApprovalWorkspaceTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Canonical authenticated decider session used by every test that exercises
    /// the API-calling branch of the page.
    /// </summary>
    private static UserSession AuthenticatedDeciderSession()
        => new(true, new ProfileOutput("d1", "Director Maria", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    [Fact]
    public void Approvals_WhenApiReturnsItems_RendersSummaryAndRows()
    {
        // Arrange — three pending decisions + chip summary.
        var summary = new ApprovalWorkspaceSummaryDto(PendingCount: 3, OverdueCount: 1, TodayCount: 2);
        var paged = new PagedResult<ApprovalQueueItemDto>(
            Items: new[]
            {
                new ApprovalQueueItemDto(
                    Id: "DoSr0001",
                    DossierCode: "D-2026-0001",
                    DecisionTitle: "Pensie pentru limită de vârstă",
                    ExaminerName: "Maria Examinator",
                    ExaminerSqid: "ExSq0001",
                    EmittedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    SlaDeadlineUtc: new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc)),
                new ApprovalQueueItemDto(
                    Id: "DoSr0002",
                    DossierCode: "D-2026-0002",
                    DecisionTitle: "Indemnizație șomaj",
                    ExaminerName: "Ion Examinator",
                    ExaminerSqid: "ExSq0002",
                    EmittedAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
                    SlaDeadlineUtc: new DateTime(2026, 5, 30, 9, 0, 0, DateTimeKind.Utc)),
                new ApprovalQueueItemDto(
                    Id: "DoSr0003",
                    DossierCode: "D-2026-0003",
                    DecisionTitle: "Alocație familială",
                    ExaminerName: null,
                    ExaminerSqid: null,
                    EmittedAtUtc: new DateTime(2026, 5, 24, 11, 0, 0, DateTimeKind.Utc),
                    SlaDeadlineUtc: null),
            },
            Page: 1, PageSize: 20, TotalCount: 3);

        _mock.When("https://api.test/api/approvals/summary")
            .Respond("application/json", JsonSerializer.Serialize(summary));
        _mock.When("https://api.test/api/approvals/pending*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        // Act
        var cut = RenderComponent<ApprovalWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedDeciderSession()));

        // Assert
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='heading']").Should().NotBeNull();
            cut.Find("[data-testid='approval-summary']").Should().NotBeNull();
            cut.Find("[data-testid='chip-pending-value']").TextContent.Should().Be("3");
            cut.Find("[data-testid='chip-overdue-value']").TextContent.Should().Be("1");
            cut.Find("[data-testid='chip-today-value']").TextContent.Should().Be("2");

            cut.Find("[data-testid='approval-queue-table']").Should().NotBeNull();
            var rows = cut.FindAll("[data-testid='approval-row']");
            rows.Count.Should().Be(3);
            cut.Markup.Should().Contain("Pensie pentru limită de vârstă");
            cut.Markup.Should().Contain("D-2026-0001");
            cut.Markup.Should().Contain("Maria Examinator");
        });
    }

    [Fact]
    public void Approvals_WhenEmpty_ShowsEmptyMessage_AndZeroChips()
    {
        var summary = new ApprovalWorkspaceSummaryDto(PendingCount: 0, OverdueCount: 0, TodayCount: 0);
        var empty = new PagedResult<ApprovalQueueItemDto>(
            Items: Array.Empty<ApprovalQueueItemDto>(),
            Page: 1, PageSize: 20, TotalCount: 0);

        _mock.When("https://api.test/api/approvals/summary")
            .Respond("application/json", JsonSerializer.Serialize(summary));
        _mock.When("https://api.test/api/approvals/pending*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        var cut = RenderComponent<ApprovalWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedDeciderSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='empty-queue']").Should().NotBeNull();
            cut.FindAll("[data-testid='approval-row']").Count.Should().Be(0);
            cut.FindAll("[data-testid='approval-queue-table']").Count.Should().Be(0);
            cut.Find("[data-testid='chip-pending-value']").TextContent.Should().Be("0");
        });
    }

    [Fact]
    public void Approvals_WhenApiFails_ShowsErrorAlert()
    {
        const string problemBody = "approval queue read failed";
        _mock.When("https://api.test/api/approvals/summary")
            .Respond(HttpStatusCode.InternalServerError, "text/plain", problemBody);

        var cut = RenderComponent<ApprovalWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedDeciderSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='heading']").Should().NotBeNull();
            cut.Find("[data-testid='error-alert']").Should().NotBeNull();
            cut.Find("[data-testid='error-detail']").TextContent.Should().Contain(problemBody);
            cut.FindAll("[data-testid='approval-queue-table']").Count.Should().Be(0);
        });
    }

    [Fact]
    public void Approvals_WhenAnonymous_RendersAnonymousMessage()
    {
        var cut = RenderComponent<ApprovalWorkspace>(
            p => p.Add(s => s.Session, UserSession.Anonymous));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='anonymous-message']").Should().NotBeNull();
            cut.FindAll("[data-testid='approval-queue-table']").Count.Should().Be(0);
            cut.FindAll("[data-testid='approval-summary']").Count.Should().Be(0);
        });
    }

    [Fact]
    public void Approvals_ApproveButton_TriggersApproveEndpoint()
    {
        // Arrange — single pending row.
        var summary = new ApprovalWorkspaceSummaryDto(PendingCount: 1, OverdueCount: 0, TodayCount: 0);
        var paged = new PagedResult<ApprovalQueueItemDto>(
            Items: new[]
            {
                new ApprovalQueueItemDto(
                    Id: "DoSrApprv1",
                    DossierCode: "D-2026-AP1",
                    DecisionTitle: "Pensie",
                    ExaminerName: "Maria",
                    ExaminerSqid: "Ex1",
                    EmittedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    SlaDeadlineUtc: new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc)),
            },
            Page: 1, PageSize: 20, TotalCount: 1);

        _mock.When("https://api.test/api/approvals/summary")
            .Respond("application/json", JsonSerializer.Serialize(summary));
        _mock.When("https://api.test/api/approvals/pending*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        // The mock asserts the approve endpoint was hit with the expected sqid.
        var approveHit = _mock
            .When(HttpMethod.Post, "https://api.test/api/decisions/DoSrApprv1/approve")
            .Respond(HttpStatusCode.OK);

        var cut = RenderComponent<ApprovalWorkspace>(
            p => p.Add(s => s.Session, AuthenticatedDeciderSession()));

        // Wait for the table to materialise so the click can fire.
        cut.WaitForAssertion(() => cut.Find("[data-testid='approve-button']").Should().NotBeNull());

        cut.Find("[data-testid='approve-button']").Click();

        cut.WaitForAssertion(() =>
            _mock.GetMatchCount(approveHit).Should().BeGreaterThanOrEqualTo(1,
                "clicking Approve must POST to /api/decisions/{sqid}/approve"));
    }
}
