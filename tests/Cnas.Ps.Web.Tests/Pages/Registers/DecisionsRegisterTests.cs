using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages.Registers;

/// <summary>
/// R1601 + R1604 / TOR Annex 3.9 — bUnit tests for the
/// <c>/registers/decisions</c> page.
/// </summary>
public sealed class DecisionsRegisterTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>Wires bUnit with the API client + HTTP mock.</summary>
    public DecisionsRegisterTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PagedResult<DecisionRegisterRowDto> SamplePage() =>
        new(new[]
        {
            new DecisionRegisterRowDto(
                Sqid: "SQID-1",
                DecisionNumber: "DEC-2026-000001",
                DecisionTypeCode: "DECIZIE_RECUPERARE_SUME",
                BeneficiaryIdnp: null,
                IssuedAtUtc: new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc),
                EffectiveFromDate: null,
                EffectiveToDate: null,
                Amount: 1500m,
                Status: "ISSUED"),
        }, 1, 20, 1);

    /// <summary>Happy path — renders the heading + register table.</summary>
    [Fact]
    public void Decisions_RendersHeadingAndTable()
    {
        _mock.When("https://api.test/api/registers/decisions*")
            .Respond("application/json", JsonSerializer.Serialize(SamplePage()));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Registers.DecisionsRegister>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='decisions-register-heading']").Should().NotBeNull();
            cut.FindAll("[data-testid='decisions-register-row']").Should().HaveCount(1);
        });
    }

    /// <summary>Filters strip + export buttons are visible per R1604.</summary>
    [Fact]
    public void Decisions_RendersFiltersAndExports()
    {
        _mock.When("https://api.test/api/registers/decisions*")
            .Respond("application/json", JsonSerializer.Serialize(
                new PagedResult<DecisionRegisterRowDto>(Array.Empty<DecisionRegisterRowDto>(), 1, 20, 0)));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Registers.DecisionsRegister>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='decisions-register-filters']").Should().NotBeNull();
            cut.Find("[data-testid='decisions-filter-type']").Should().NotBeNull();
            cut.Find("[data-testid='decisions-export-csv']").Should().NotBeNull();
            cut.Find("[data-testid='decisions-export-xlsx']").Should().NotBeNull();
            cut.Find("[data-testid='decisions-export-pdf']").Should().NotBeNull();
        });
    }
}
