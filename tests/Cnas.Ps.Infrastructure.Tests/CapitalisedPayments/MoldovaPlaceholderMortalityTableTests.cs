using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.CapitalisedPayments;

namespace Cnas.Ps.Infrastructure.Tests.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — unit tests for the placeholder mortality table.
/// Covers the standard lookups, the female &gt; male invariant, and the
/// out-of-range guard.
/// </summary>
public sealed class MoldovaPlaceholderMortalityTableTests
{
    [Fact]
    public void Male30_ReturnsExpectedMonths()
    {
        var table = new MoldovaPlaceholderMortalityTable();

        var result = table.GetRemainingLifeExpectancyMonths(BeneficiarySex.Male, 30);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(564);
    }

    [Fact]
    public void Female30_GreaterThanMale30()
    {
        var table = new MoldovaPlaceholderMortalityTable();

        var male = table.GetRemainingLifeExpectancyMonths(BeneficiarySex.Male, 30);
        var female = table.GetRemainingLifeExpectancyMonths(BeneficiarySex.Female, 30);

        male.IsSuccess.Should().BeTrue();
        female.IsSuccess.Should().BeTrue();
        female.Value.Should().BeGreaterThan(male.Value);
    }

    [Fact]
    public void Age111_ReturnsValidationFailure()
    {
        var table = new MoldovaPlaceholderMortalityTable();

        var result = table.GetRemainingLifeExpectancyMonths(BeneficiarySex.Male, 111);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(MoldovaPlaceholderMortalityTable.AgeOutOfRangeCode);
    }

    [Fact]
    public void IntermediateAge_LinearlyInterpolated()
    {
        var table = new MoldovaPlaceholderMortalityTable();

        // Halfway between male age 30 (564) and male age 60 (222) is ~45.
        var result = table.GetRemainingLifeExpectancyMonths(BeneficiarySex.Male, 45);

        result.IsSuccess.Should().BeTrue();
        // Linear interpolation: 564 + (222 - 564) * (45-30)/(60-30) = 564 - 171 = 393.
        result.Value.Should().BeInRange(380, 410);
    }
}
