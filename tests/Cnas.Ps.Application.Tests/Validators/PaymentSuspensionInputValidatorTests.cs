using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1504 / TOR §3.7-E — pins the contract enforced by
/// <see cref="PaymentSuspensionInputValidator"/>.
/// </summary>
public sealed class PaymentSuspensionInputValidatorTests
{
    private readonly PaymentSuspensionInputValidator _sut = new();

    /// <summary>Happy path — a well-formed input is accepted.</summary>
    [Fact]
    public void Valid_Input_HasNoErrors()
    {
        var input = new PaymentSuspensionInputDto("Certificat medical expirat.");
        var result = _sut.TestValidate(input);
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Reason shorter than 3 characters is rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void Reason_TooShort_Fails(string reason)
    {
        var input = new PaymentSuspensionInputDto(reason);
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Reason);
    }

    /// <summary>Reason longer than 500 characters is rejected.</summary>
    [Fact]
    public void Reason_TooLong_Fails()
    {
        var input = new PaymentSuspensionInputDto(new string('x', 501));
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Reason);
    }

    /// <summary>Reason at the 500-character boundary is accepted.</summary>
    [Fact]
    public void Reason_AtUpperBound_IsAccepted()
    {
        var input = new PaymentSuspensionInputDto(new string('x', 500));
        var result = _sut.TestValidate(input);
        result.IsValid.Should().BeTrue();
    }
}
