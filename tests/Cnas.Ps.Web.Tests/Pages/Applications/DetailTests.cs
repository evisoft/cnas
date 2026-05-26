using System.Net;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Applications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Applications;

/// <summary>Tests for the citizen application <see cref="Detail"/> page.</summary>
public sealed class DetailTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public DetailTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void StubGet(string id, string status)
    {
        var app = new ApplicationOutput(id, status, "REF-99", DateTime.UtcNow);
        _mock.When($"https://api.test/api/applications/{id}")
            .Respond("application/json", JsonSerializer.Serialize(app));
    }

    [Fact]
    public void Detail_WhenStatusIsApproved_HidesWithdrawButton()
    {
        StubGet("abc", "Approved");

        var cut = RenderComponent<Detail>(p => p.Add(d => d.Id, "abc"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='status-badge']").Should().NotBeNull());
        cut.FindAll("[data-testid='withdraw-btn']").Count.Should().Be(0);
    }

    [Fact]
    public void Detail_WhenStatusIsSubmitted_ShowsWithdrawButton()
    {
        StubGet("abc", "Submitted");

        var cut = RenderComponent<Detail>(p => p.Add(d => d.Id, "abc"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='withdraw-btn']").Should().NotBeNull());
    }

    [Fact]
    public void Detail_OnWithdrawClick_CallsApiAndNavigatesBack()
    {
        StubGet("abc", "Submitted");
        _mock.When(HttpMethod.Post, "https://api.test/api/applications/abc/withdraw")
            .Respond(HttpStatusCode.NoContent);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = RenderComponent<Detail>(p => p.Add(d => d.Id, "abc"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='withdraw-btn']").Should().NotBeNull());
        cut.Find("[data-testid='withdraw-btn']").Click();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/applications"));
    }
}
