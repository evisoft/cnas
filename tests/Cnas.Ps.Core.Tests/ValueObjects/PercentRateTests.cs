using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class PercentRateTests
{
    [Fact]
    public void TryCreate_Zero_Succeeds()
    {
        var r = PercentRate.TryCreate(0m);

        r.IsSuccess.Should().BeTrue();
        r.Value.Value.Should().Be(0m);
    }

    [Fact]
    public void TryCreate_OneHundred_Succeeds()
    {
        var r = PercentRate.TryCreate(100m);

        r.IsSuccess.Should().BeTrue();
        r.Value.Value.Should().Be(100m);
    }

    [Fact]
    public void TryCreate_TypicalMidValue_Succeeds()
    {
        var r = PercentRate.TryCreate(22.5m);

        r.IsSuccess.Should().BeTrue();
        r.Value.Value.Should().Be(22.5m);
    }

    [Fact]
    public void TryCreate_Negative_Fails()
    {
        var r = PercentRate.TryCreate(-0.01m);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.InvalidPercentRate);
    }

    [Fact]
    public void TryCreate_GreaterThan100_Fails()
    {
        var r = PercentRate.TryCreate(100.01m);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.InvalidPercentRate);
    }

    [Fact]
    public void TryCreate_RoundsToFourDecimals()
    {
        var r = PercentRate.TryCreate(12.345678m);

        r.IsSuccess.Should().BeTrue();
        r.Value.Value.Should().Be(12.3457m);
    }

    [Fact]
    public void Apply_OnMoney_ReturnsRoundedShareInSameCurrency()
    {
        var rate = PercentRate.TryCreate(22.5m).Value;
        var salary = Money.Mdl(1000m);

        var contribution = rate.Apply(salary);

        contribution.Amount.Should().Be(225m);
        contribution.CurrencyCode.Should().Be("MDL");
    }

    [Fact]
    public void Apply_RoundsToCurrencyScale()
    {
        var rate = PercentRate.TryCreate(33.3333m).Value;
        var salary = Money.Mdl(100m);

        var contribution = rate.Apply(salary);

        // 100 * 0.333333 = 33.3333 → rounds to 33.33 (MDL scale = 2)
        contribution.Amount.Should().Be(33.33m);
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var a = PercentRate.TryCreate(22.5m).Value;
        var b = PercentRate.TryCreate(22.5m).Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_AppendsPercentSign()
    {
        var r = PercentRate.TryCreate(22.5m).Value;

        r.ToString().Should().Be("22.5%");
    }
}
