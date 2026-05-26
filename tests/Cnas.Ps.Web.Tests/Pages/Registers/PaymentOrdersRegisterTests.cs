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
/// R1602 + R1604 / TOR Annex 3.10 — bUnit tests for the
/// <c>/registers/payment-orders</c> page.
/// </summary>
public sealed class PaymentOrdersRegisterTests : TestContext
{
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>Wires bUnit with the API client + HTTP mock.</summary>
    public PaymentOrdersRegisterTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PagedResult<BeneficiaryPaymentAccountRowDto> SamplePage() =>
        new(new[]
        {
            new BeneficiaryPaymentAccountRowDto(
                Sqid: "SQID-1",
                BeneficiaryIdnpHash: "HASH:X",
                PaymentMethod: "MPAY_IBAN",
                Iban: "MD24 **** **** **** XXXX 1234",
                LastPaymentAtUtc: new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc),
                TotalPaidYtd: 750m,
                Status: "ACTIVE"),
        }, 1, 20, 1);

    /// <summary>Happy path — renders the heading + register table.</summary>
    [Fact]
    public void PaymentOrders_RendersHeadingAndTable()
    {
        _mock.When("https://api.test/api/registers/payment-accounts*")
            .Respond("application/json", JsonSerializer.Serialize(SamplePage()));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Registers.PaymentOrdersRegister>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='payment-orders-register-heading']").Should().NotBeNull();
            var rows = cut.FindAll("[data-testid='payment-orders-register-row']");
            rows.Should().HaveCount(1);
            // SEC 035 — IBAN is rendered masked.
            rows[0].TextContent.Should().Contain("*");
        });
    }

    /// <summary>Filters strip + export buttons are visible per R1604.</summary>
    [Fact]
    public void PaymentOrders_RendersFiltersAndExports()
    {
        _mock.When("https://api.test/api/registers/payment-accounts*")
            .Respond("application/json", JsonSerializer.Serialize(
                new PagedResult<BeneficiaryPaymentAccountRowDto>(
                    Array.Empty<BeneficiaryPaymentAccountRowDto>(), 1, 20, 0)));

        var cut = RenderComponent<Cnas.Ps.Web.Pages.Registers.PaymentOrdersRegister>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='payment-orders-register-filters']").Should().NotBeNull();
            cut.Find("[data-testid='payment-orders-filter-idnp']").Should().NotBeNull();
            cut.Find("[data-testid='payment-orders-export-csv']").Should().NotBeNull();
            cut.Find("[data-testid='payment-orders-export-xlsx']").Should().NotBeNull();
            cut.Find("[data-testid='payment-orders-export-pdf']").Should().NotBeNull();
        });
    }
}
