using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void TryCreate_WithValidMdl_Succeeds()
    {
        var result = Money.TryCreate(100.00m, "MDL");

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(100.00m);
        result.Value.CurrencyCode.Should().Be("MDL");
    }

    [Fact]
    public void TryCreate_LowercaseCurrency_NormalisesToUpper()
    {
        var result = Money.TryCreate(50m, "eur");

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrencyCode.Should().Be("EUR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("MD")]
    [InlineData("EURO")]
    [InlineData("M1L")]
    public void TryCreate_InvalidCurrencyFormat_Fails(string? code)
    {
        var result = Money.TryCreate(10m, code!);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidMoneyCurrency);
    }

    [Fact]
    public void TryCreate_UnknownIsoCurrency_Fails()
    {
        // ZZZ is reserved but not a real currency; we restrict to the supported scale table.
        var result = Money.TryCreate(10m, "ZZZ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidMoneyCurrency);
    }

    [Fact]
    public void TryCreate_RoundsToCurrencyScale_BankersRounding()
    {
        // Banker's rounding: 1.005 → 1.00 (round to even), 1.015 → 1.02
        var a = Money.TryCreate(1.005m, "MDL").Value;
        var b = Money.TryCreate(1.015m, "MDL").Value;

        a.Amount.Should().Be(1.00m);
        b.Amount.Should().Be(1.02m);
    }

    [Fact]
    public void TryCreate_RoundsToZeroScale_ForJpy()
    {
        // JPY is supported with scale 0.
        var m = Money.TryCreate(123.7m, "JPY").Value;

        m.Amount.Should().Be(124m);
    }

    [Fact]
    public void Mdl_StaticFactory_ProducesMdlMoney()
    {
        var m = Money.Mdl(42.50m);

        m.Amount.Should().Be(42.50m);
        m.CurrencyCode.Should().Be("MDL");
    }

    [Fact]
    public void Addition_SameCurrency_SumsAmounts()
    {
        var a = Money.Mdl(10m);
        var b = Money.Mdl(2.50m);

        var sum = a + b;

        sum.Amount.Should().Be(12.50m);
        sum.CurrencyCode.Should().Be("MDL");
    }

    [Fact]
    public void Subtraction_SameCurrency_Subtracts()
    {
        var diff = Money.Mdl(10m) - Money.Mdl(3.25m);

        diff.Amount.Should().Be(6.75m);
    }

    [Fact]
    public void Multiplication_ByScalar_Multiplies()
    {
        var product = Money.Mdl(10m) * 1.5m;

        product.Amount.Should().Be(15m);
    }

    [Fact]
    public void Division_ByScalar_Divides()
    {
        var quotient = Money.Mdl(10m) / 4m;

        quotient.Amount.Should().Be(2.50m);
    }

    [Fact]
    public void Addition_DifferentCurrencies_Throws()
    {
        var mdl = Money.Mdl(10m);
        var eur = Money.TryCreate(10m, "EUR").Value;

        var act = () => _ = mdl + eur;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Subtraction_DifferentCurrencies_Throws()
    {
        var mdl = Money.Mdl(10m);
        var eur = Money.TryCreate(10m, "EUR").Value;

        var act = () => _ = mdl - eur;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Equality_SameAmountAndCurrency_AreEqual()
    {
        var a = Money.Mdl(100m);
        var b = Money.Mdl(100m);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentCurrency_NotEqual()
    {
        var a = Money.Mdl(100m);
        var b = Money.TryCreate(100m, "EUR").Value;

        a.Should().NotBe(b);
    }

    [Fact]
    public void ToString_ReturnsAmountSpaceCurrency()
    {
        var m = Money.Mdl(123.45m);

        m.ToString().Should().Be("123.45 MDL");
    }

    [Fact]
    public void TryCreate_NegativeAmount_Succeeds()
    {
        // Negative allowed: refunds and corrections legitimately occur.
        var m = Money.TryCreate(-50m, "MDL");

        m.IsSuccess.Should().BeTrue();
        m.Value.Amount.Should().Be(-50m);
    }

    [Fact]
    public void TryCreate_ZeroAmount_Succeeds()
    {
        var m = Money.TryCreate(0m, "MDL");

        m.IsSuccess.Should().BeTrue();
        m.Value.Amount.Should().Be(0m);
    }
}
