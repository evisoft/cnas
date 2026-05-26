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
/// R0200 / TOR CF 20.01-03, MR 012 — bUnit tests for the
/// <see cref="CronSchedules"/> page. Each test stubs the
/// <c>GET /api/automation/schedules</c> endpoint to pin one behaviour.
/// </summary>
public sealed class CronSchedulesTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Composes the test container with mocked HTTP, the API client, and a
    /// CnasTechAdmin-authorised user so the page's
    /// <c>[Authorize(Policy = "CnasTechAdmin")]</c> gate passes.
    /// </summary>
    public CronSchedulesTests()
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
    public void Page_WhenNoJobs_RendersEmptyState()
    {
        _mock.When("https://api.test/api/automation/schedules")
            .Respond("application/json", "[]");

        var cut = RenderComponent<CronSchedules>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='cron-empty-state']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Page_WhenJobsPresent_RendersOneRowPerJob()
    {
        var schedules = new[]
        {
            new JobScheduleOverrideDto(
                Id: null,
                JobCode: "mpay-dispatcher",
                CronExpression: "0 0/5 * * * ?",
                DefaultCronExpression: "0 0/5 * * * ?",
                IsPaused: false,
                IsOverridden: false,
                UpdatedAtUtc: null,
                UpdatedByUserSqid: null),
            new JobScheduleOverrideDto(
                Id: "SQID-42",
                JobCode: "mconnect-sync",
                CronExpression: "0 0 4 * * ?",
                DefaultCronExpression: "0 0 3 * * ?",
                IsPaused: true,
                IsOverridden: true,
                UpdatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                UpdatedByUserSqid: "SQID-1"),
        };
        _mock.When("https://api.test/api/automation/schedules")
            .Respond("application/json", JsonSerializer.Serialize(schedules));

        var cut = RenderComponent<CronSchedules>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='cron-row']").Count.Should().Be(2);
            // The paused job offers a Resume button.
            cut.FindAll("[data-testid='cron-resume-mconnect-sync']").Count.Should().Be(1);
            // The non-paused job offers a Pause button.
            cut.FindAll("[data-testid='cron-pause-mpay-dispatcher']").Count.Should().Be(1);
        });
    }

    [Fact]
    public void Page_WhenLoadFails_RendersErrorAlert()
    {
        _mock.When("https://api.test/api/automation/schedules")
            .Respond(HttpStatusCode.InternalServerError);

        var cut = RenderComponent<CronSchedules>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='cron-error']").Count.Should().Be(1);
        });
    }
}
