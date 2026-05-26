using System.Net;
using System.Net.Http;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages.Decisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Decisions;

/// <summary>
/// R0932 / TOR §10.1 — bUnit coverage for the Fișa de calcul interactive editor.
/// </summary>
public sealed class FisaDeCalculTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    public FisaDeCalculTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RecalcButton_PostsToEndpoint_AndUpdatesTotal()
    {
        var resp = new FisaDeCalculRecalcResultDto(
            DossierSqid: "DOSS-1",
            TotalAmountMdl: 9000.00m,
            Rows: new List<FisaDeCalculRowDto>
            {
                new("2024-01", 3000.00m),
                new("2024-02", 3000.00m),
                new("2024-03", 3000.00m),
            });
        _mock.When(HttpMethod.Post, "https://api.test/api/decisions/fisa-de-calcul/recalc")
            .Respond("application/json", JsonSerializer.Serialize(resp));

        var cut = RenderComponent<FisaDeCalcul>(p => p.Add(x => x.Sqid, "DOSS-1"));
        cut.Find("[data-testid='fisa-recalc-button']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='fisa-total']").TextContent.Should().Contain("9000.00");
        });
    }

    [Fact]
    public void RecalcButton_OnTransportFailure_RendersErrorAlert()
    {
        _mock.When(HttpMethod.Post, "https://api.test/api/decisions/fisa-de-calcul/recalc")
            .Respond(HttpStatusCode.BadRequest);

        var cut = RenderComponent<FisaDeCalcul>(p => p.Add(x => x.Sqid, "DOSS-X"));
        cut.Find("[data-testid='fisa-recalc-button']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='error-alert']").Count.Should().Be(1);
        });
    }
}
