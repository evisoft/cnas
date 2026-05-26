using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0301 / R0311 — input-DTO validation tests for the linked-entity contracts.
/// Each test pair captures a positive/negative case to lock the rule down.
/// </summary>
public class LinkedEntitiesValidatorsTests
{
    [Theory]
    [InlineData("MD", true)]
    [InlineData("RO", true)]
    [InlineData("Md", false)]  // lowercase – rejected
    [InlineData("MDA", false)] // 3-letter – rejected
    [InlineData("MD1", false)] // alphanumeric – rejected
    public void PayerAddress_CountryValidation(string country, bool expectValid)
    {
        var v = new PayerAddressInputDtoValidator();
        var dto = new PayerAddressInputDto("S", "C", "R", "MD2001", country);

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("M.69.10", true)]
    [InlineData("A.01.01", true)]
    [InlineData("M6910", false)]    // missing dots
    [InlineData("m.69.10", false)]  // lowercase letter
    [InlineData("M.691.0", false)]  // wrong grouping
    public void PayerActivityCaem_CodeValidation(string code, bool expectValid)
    {
        var v = new PayerActivityCaemInputDtoValidator();
        var dto = new PayerActivityCaemInputDto(code, "desc", false);

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(15000.50, true)]
    [InlineData(-1.0, false)]
    [InlineData(2_000_000.0, false)]
    public void ContributorActivityPeriod_MonthlySalaryBounds(decimal salary, bool expectValid)
    {
        var v = new ContributorActivityPeriodInputDtoValidator();
        var dto = new ContributorActivityPeriodInputDto("EMP-1", "Position", salary);

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void ContributorSocialInsuranceContract_EndBeforeStart_Fails()
    {
        var v = new ContributorSocialInsuranceContractInputDtoValidator();
        var dto = new ContributorSocialInsuranceContractInputDto(
            "C-1", new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1), 100m, null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("AGRNMD2X", true)]
    [InlineData("AGRNMD2XXXX", true)]
    [InlineData("123456ABC", false)]  // does not start with 6 letters
    [InlineData("agrnmd2x", false)]   // lowercase
    [InlineData("AGRN", false)]       // too short
    public void PayerBankAccount_BicValidation(string bic, bool expectValid)
    {
        var v = new PayerBankAccountInputDtoValidator();
        var dto = new PayerBankAccountInputDto(
            "Holder", "MD24AG000000022500931776", "Bank", bic, true, "MDL");

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("MDL", true)]
    [InlineData("EUR", true)]
    [InlineData("usd", false)]    // lowercase rejected
    [InlineData("MD", false)]     // 2-letter
    [InlineData("MDLL", false)]   // 4-letter
    public void PayerBankAccount_CurrencyValidation(string currency, bool expectValid)
    {
        var v = new PayerBankAccountInputDtoValidator();
        var dto = new PayerBankAccountInputDto(
            "Holder", "MD24AG000000022500931776", "Bank", "AGRNMD2X", true, currency);

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("MD24AG000000022500931776", true)]
    [InlineData("md24 ag00 0000 0225 0093 1776", true)]   // canonicalised
    [InlineData("NOT-AN-IBAN", false)]
    [InlineData("MD24", false)]   // too short overall
    public void PayerBankAccount_IbanValidation(string iban, bool expectValid)
    {
        var v = new PayerBankAccountInputDtoValidator();
        var dto = new PayerBankAccountInputDto(
            "Holder", iban, "Bank", "AGRNMD2X", true, "MDL");

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("Andrei P.", "Accountant", true)]
    [InlineData("", "Accountant", false)]      // empty name
    [InlineData("Andrei P.", null, true)]      // role optional
    public void PayerSecondaryContact_BasicValidation(string name, string? role, bool expectValid)
    {
        var v = new PayerSecondaryContactInputDtoValidator();
        var dto = new PayerSecondaryContactInputDto(name, role, null, null);

        var result = v.Validate(dto);

        result.IsValid.Should().Be(expectValid);
    }
}
