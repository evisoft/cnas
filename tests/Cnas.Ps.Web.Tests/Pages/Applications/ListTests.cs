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

/// <summary>Tests for the citizen application <see cref="List"/> page.</summary>
public sealed class ListTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public ListTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void StubMine(int totalCount, int page = 1, int pageSize = 20)
    {
        var items = new List<ApplicationListItemOutput>();
        for (int i = 0; i < Math.Min(pageSize, totalCount); i++)
        {
            items.Add(new ApplicationListItemOutput($"id{i}", "Submitted", $"REF-{i:000}", "u1", DateTime.UtcNow));
        }
        var paged = new PagedResult<ApplicationListItemOutput>(items, page, pageSize, totalCount);
        _mock.When("https://api.test/api/applications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));
    }

    [Fact]
    public void List_RendersPagedRows()
    {
        StubMine(totalCount: 3);

        var cut = RenderComponent<List>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid='row']");
            rows.Count.Should().Be(3);
        });
    }

    [Fact]
    public void List_RowClick_NavigatesToDetail()
    {
        StubMine(totalCount: 1);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = RenderComponent<List>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='row']").Should().NotBeNull());
        cut.Find("[data-testid='row']").Click();

        nav.Uri.Should().EndWith("/applications/id0");
    }
}
