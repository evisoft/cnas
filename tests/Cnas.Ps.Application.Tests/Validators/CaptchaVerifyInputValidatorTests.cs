using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// iter-149 / Fix 11 — rule-by-rule tests for
/// <see cref="CaptchaVerifyInputValidator"/>. Pins the presence + length
/// caps on both the opaque challenge token and the user-supplied answer.
/// </summary>
public sealed class CaptchaVerifyInputValidatorTests
{
    [Fact]
    public void Validate_HappyPath_Succeeds()
    {
        var sut = new CaptchaVerifyInputValidator();

        var result = sut.Validate(new CaptchaVerifyInputDto("opaque-token-abc", "42xyz"));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("", "answer")]
    [InlineData("   ", "answer")]
    [InlineData("token", "")]
    [InlineData("token", "   ")]
    [InlineData("", "")]
    public void Validate_EmptyFields_Fail(string token, string answer)
    {
        var sut = new CaptchaVerifyInputValidator();

        var result = sut.Validate(new CaptchaVerifyInputDto(token, answer));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OversizedChallengeToken_Fails()
    {
        var sut = new CaptchaVerifyInputValidator();
        // One character above the documented cap.
        var oversizedToken = new string('a', CaptchaVerifyInputValidator.MaxChallengeTokenLength + 1);

        var result = sut.Validate(new CaptchaVerifyInputDto(oversizedToken, "ok"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CaptchaVerifyInputDto.ChallengeToken));
    }

    [Fact]
    public void Validate_OversizedAnswer_Fails()
    {
        var sut = new CaptchaVerifyInputValidator();
        var oversizedAnswer = new string('a', CaptchaVerifyInputValidator.MaxAnswerLength + 1);

        var result = sut.Validate(new CaptchaVerifyInputDto("ok", oversizedAnswer));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CaptchaVerifyInputDto.Answer));
    }
}
