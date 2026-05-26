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
/// R0204 / TOR CF 20.07-08 — bUnit tests for the
/// <see cref="JobsDashboard"/> page. Each test stubs the
/// <c>GET /api/automation/jobs/state</c> + <c>GET /api/admin/failed-jobs</c>
/// endpoints to pin one behaviour.
/// </summary>
public sealed class JobsDashboardTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Composes the test container with mocked HTTP, the API client, localization,
    /// loose JS interop, AND a CnasTechAdmin-authorised user so the page's
    /// <c>[Authorize(Policy = "CnasTechAdmin")]</c> gate passes.
    /// </summary>
    public JobsDashboardTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("techadmin");
        auth.SetPolicies("CnasTechAdmin");
    }

    [Fact]
    public void Dashboard_WhenNoJobs_RendersEmptyState()
    {
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond("application/json", "[]");
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(Array.Empty<FailedJobOutput>()));

        var cut = RenderComponent<JobsDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-empty-state']").Count.Should().Be(1);
            cut.FindAll("[data-testid='failed-empty-state']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Dashboard_WhenJobsPresent_RendersOneRowPerJob()
    {
        var jobs = new[]
        {
            new JobStateDto(
                JobName: "mpay-dispatcher",
                JobGroup: "DEFAULT",
                TriggerName: "mpay-dispatcher-trigger",
                NextFireUtc: new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc),
                LastFireUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
                State: "Normal"),
            new JobStateDto(
                JobName: "mconnect-sync",
                JobGroup: "DEFAULT",
                TriggerName: "mconnect-sync-trigger",
                NextFireUtc: new DateTime(2026, 5, 25, 3, 0, 0, DateTimeKind.Utc),
                LastFireUtc: null,
                State: "Paused"),
        };
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond("application/json", JsonSerializer.Serialize(jobs));
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(Array.Empty<FailedJobOutput>()));

        var cut = RenderComponent<JobsDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-state-row']").Count.Should().Be(2);
        });
    }

    [Fact]
    public void Dashboard_WhenFailedJobsPresent_RendersOneRowPerFailedJob()
    {
        var failed = new[]
        {
            new FailedJobOutput(
                Id: "JOB-SQID-1",
                JobName: "mpay-dispatcher",
                JobGroup: "DEFAULT",
                FailedAtUtc: new DateTime(2026, 5, 24, 12, 30, 0, DateTimeKind.Utc),
                ExceptionType: "System.Net.Http.HttpRequestException",
                ExceptionMessage: "timeout reaching MPay",
                StackTrace: null,
                RefireCount: 0,
                ReplayState: null,
                LastReplayAtUtc: null),
        };
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond("application/json", "[]");
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(failed));

        var cut = RenderComponent<JobsDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='failed-jobs-row']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Dashboard_StateLoadFailure_RendersErrorAlert()
    {
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond(HttpStatusCode.InternalServerError);
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(Array.Empty<FailedJobOutput>()));

        var cut = RenderComponent<JobsDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-error']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Dashboard_ConsumerFilter_NarrowsActiveTriggers()
    {
        // R1812 — selecting the "rsp-" consumer prefix should hide jobs whose
        // job name does not start with that prefix.
        var jobs = new[]
        {
            new JobStateDto("rsp-sync", "DEFAULT", "rsp-sync-trigger", null, null, "Normal"),
            new JobStateDto("sfs-sync", "DEFAULT", "sfs-sync-trigger", null, null, "Normal"),
        };
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond("application/json", JsonSerializer.Serialize(jobs));
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(Array.Empty<FailedJobOutput>()));

        var cut = RenderComponent<JobsDashboard>();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-state-row']").Count.Should().Be(2);
        });

        var select = cut.Find("[data-testid='consumer-filter']");
        select.Change("rsp-");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-state-row']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Dashboard_ActiveTriggersRow_RendersRunNowButRemovesReplay()
    {
        // The active-triggers row previously rendered both a "Run now" and a
        // duplicate "Replay" button that wired to the same TriggerAsync handler.
        // The duplicate has been removed (DLQ "Retry" lives in the failed-jobs
        // table) — confirm the row only exposes the canonical "Run now" affordance
        // and that triggering it still POSTs to /api/automation/{code}/run-now.
        var jobs = new[]
        {
            new JobStateDto("rsp-sync", "DEFAULT", "rsp-sync-trigger", null, null, "Normal"),
        };
        _mock.When("https://api.test/api/automation/jobs/state")
            .Respond("application/json", JsonSerializer.Serialize(jobs));
        _mock.When("https://api.test/api/admin/failed-jobs*")
            .Respond("application/json", SerializeFailedPage(Array.Empty<FailedJobOutput>()));
        var runNowRequest = _mock.When(HttpMethod.Post, "https://api.test/api/automation/rsp-sync/run-now")
            .Respond(HttpStatusCode.Accepted);

        var cut = RenderComponent<JobsDashboard>();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='jobs-run-now']").Count.Should().Be(1);
            cut.FindAll("[data-testid='jobs-replay']").Count.Should().Be(0);
        });

        cut.Find("[data-testid='jobs-run-now']").Click();

        cut.WaitForAssertion(() =>
        {
            _mock.GetMatchCount(runNowRequest).Should().BeGreaterThan(0);
        });
    }

    private static string SerializeFailedPage(IReadOnlyList<FailedJobOutput> items)
    {
        var page = new PagedResult<FailedJobOutput>(items, Page: 1, PageSize: 50, TotalCount: items.Count);
        return JsonSerializer.Serialize(page);
    }
}
