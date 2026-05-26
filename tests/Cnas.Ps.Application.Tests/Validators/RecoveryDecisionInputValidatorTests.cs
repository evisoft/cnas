using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1505 / TOR §3.7-F — pins the contract enforced by
/// <see cref="RecoveryDecisionInputValidator"/>.
/// </summary>
public sealed class RecoveryDecisionInputValidatorTests
{
    private readonly RecoveryDecisionInputValidator _sut = new();

    /// <summary>Happy path — a well-formed input is accepted.</summary>
    [Fact]
    public void Valid_Input_HasNoErrors()
    {
        var input = new RecoveryDecisionInputDto(
            SolicitantSqid: "SQID-9",
            Amount: 1500m,
            Reason: "Sumă plătită necuvenit (recalcul venit asigurat).");

        var result = _sut.TestValidate(input);
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Zero or negative amount is rejected.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void NonPositive_Amount_Fails(decimal amount)
    {
        var input = new RecoveryDecisionInputDto("SQID-9", amount, "valid reason");

        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    /// <summary>Reason too short is rejected.</summary>
    [Fact]
    public void Reason_TooShort_Fails()
    {
        var input = new RecoveryDecisionInputDto("SQID-9", 100m, "ab");

        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Reason);
    }

    /// <summary>Empty solicitant Sqid is rejected.</summary>
    [Fact]
    public void Empty_SolicitantSqid_Fails()
    {
        var input = new RecoveryDecisionInputDto(string.Empty, 100m, "valid reason");

        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.SolicitantSqid);
    }

    /// <summary>Amount over the 100M cap is rejected.</summary>
    [Fact]
    public void Amount_Over_Cap_Fails()
    {
        var input = new RecoveryDecisionInputDto(
            "SQID-9", RecoveryDecisionInputValidator.MaxAmount + 1m, "valid reason");

        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }
}
