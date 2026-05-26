using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.CapitalisedPayments;

namespace Cnas.Ps.Infrastructure.Tests.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — unit tests for the pure-function present-value
/// annuity calculator. Covers the zero-discount sanity check, monotonicity in
/// discount rate, lifetime obligations + mortality-table dispatch, age range
/// guards, and the female &gt; male present-value invariant for lifetime
/// obligations.
/// </summary>
public sealed class PresentValueAnnuityCalculatorTests
{
    /// <summary>Builds a calculator with the placeholder mortality table.</summary>
    private static PresentValueAnnuityCalculator NewCalculator() =>
        new(new MoldovaPlaceholderMortalityTable());

    /// <summary>Builds a baseline input DTO targeting a fixed-end obligation.</summary>
    private static CapitalisedAnnuityInputDto FixedEndInput(decimal rate = 0m, int months = 12) => new(
        BeneficiarySex: nameof(BeneficiarySex.Male),
        AgeAtValuationYears: 45m,
        MonthlyAmountMdl: 1_000m,
        ValuationDate: new DateOnly(2026, 6, 1),
        ObligationEndDate: new DateOnly(2026, 6, 1).AddMonths(months),
        AnnualDiscountRatePercent: rate);

    [Fact]
    public void ZeroDiscount_FixedEnd_SumsMonthlyAmounts()
    {
        var calc = NewCalculator();

        var result = calc.Compute(FixedEndInput(rate: 0m, months: 12));

        result.IsSuccess.Should().BeTrue();
        result.Value.LifeExpectancyMonths.Should().Be(12);
        result.Value.CapitalisedAmountMdl.Should().Be(12_000m);
        result.Value.EffectiveDiscountMonthly.Should().Be(0m);
    }

    [Fact]
    public void HigherDiscount_ProducesLowerPresentValue()
    {
        var calc = NewCalculator();

        var low = calc.Compute(FixedEndInput(rate: 1m, months: 60));
        var high = calc.Compute(FixedEndInput(rate: 12m, months: 60));

        low.IsSuccess.Should().BeTrue();
        high.IsSuccess.Should().BeTrue();
        high.Value.CapitalisedAmountMdl.Should().BeLessThan(low.Value.CapitalisedAmountMdl);
    }

    [Fact]
    public void LifetimeObligation_UsesMortalityTable()
    {
        var calc = NewCalculator();
        var input = FixedEndInput() with
        {
            ObligationEndDate = null, // lifetime
            AgeAtValuationYears = 60m,
            AnnualDiscountRatePercent = 5m,
        };

        var result = calc.Compute(input);

        result.IsSuccess.Should().BeTrue();
        // Placeholder mortality table — male age 60 → 222 months.
        result.Value.LifeExpectancyMonths.Should().Be(222);
        result.Value.CapitalisedAmountMdl.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void AgeOutOfRange_ReturnsFailure()
    {
        var calc = NewCalculator();
        var input = FixedEndInput() with { AgeAtValuationYears = 150m };

        var result = calc.Compute(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PresentValueAnnuityCalculator.InvalidInputCode);
    }

    [Fact]
    public void FemaleLifetime_GreaterThanMaleLifetime_SameAgeSameRate()
    {
        var calc = NewCalculator();
        var male = calc.Compute(new CapitalisedAnnuityInputDto(
            BeneficiarySex: nameof(BeneficiarySex.Male),
            AgeAtValuationYears: 60m,
            MonthlyAmountMdl: 1_000m,
            ValuationDate: new DateOnly(2026, 6, 1),
            ObligationEndDate: null,
            AnnualDiscountRatePercent: 5m));
        var female = calc.Compute(new CapitalisedAnnuityInputDto(
            BeneficiarySex: nameof(BeneficiarySex.Female),
            AgeAtValuationYears: 60m,
            MonthlyAmountMdl: 1_000m,
            ValuationDate: new DateOnly(2026, 6, 1),
            ObligationEndDate: null,
            AnnualDiscountRatePercent: 5m));

        male.IsSuccess.Should().BeTrue();
        female.IsSuccess.Should().BeTrue();
        female.Value.CapitalisedAmountMdl.Should().BeGreaterThan(male.Value.CapitalisedAmountMdl);
    }

    [Fact]
    public void BadSex_ReturnsFailure()
    {
        var calc = NewCalculator();
        var input = FixedEndInput() with { BeneficiarySex = "Other" };

        var result = calc.Compute(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PresentValueAnnuityCalculator.InvalidInputCode);
    }
}
