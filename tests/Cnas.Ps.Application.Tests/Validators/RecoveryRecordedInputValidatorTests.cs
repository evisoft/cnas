using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// iter-149 / Fix 10 — rule-by-rule tests for
/// <see cref="RecoveryRecordedInputValidator"/>. Pins the strictly-positive
/// amount invariant + the upper sanity cap so future refactors cannot
/// silently weaken the gate.
/// </summary>
public sealed class RecoveryRecordedInputValidatorTests
{
    [Fact]
    public void Validate_PositiveAmount_Succeeds()
    {
        var sut = new RecoveryRecordedInputValidator();

        var result = sut.Validate(new RecoveryRecordedInputDto(1m));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_LargeButCappedAmount_Succeeds()
    {
        var sut = new RecoveryRecordedInputValidator();

        var result = sut.Validate(new RecoveryRecordedInputDto(
            RecoveryRecordedInputValidator.MaxRecoveredAmount));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_NonPositiveAmount_Fails(decimal amount)
    {
        var sut = new RecoveryRecordedInputValidator();

        var result = sut.Validate(new RecoveryRecordedInputDto(amount));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RecoveryRecordedInputDto.RecoveredAmount));
    }

    [Fact]
    public void Validate_AmountAboveCap_Fails()
    {
        var sut = new RecoveryRecordedInputValidator();
        // One MDL above the documented cap.
        var dto = new RecoveryRecordedInputDto(
            RecoveryRecordedInputValidator.MaxRecoveredAmount + 1m);

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RecoveryRecordedInputDto.RecoveredAmount));
    }
}
