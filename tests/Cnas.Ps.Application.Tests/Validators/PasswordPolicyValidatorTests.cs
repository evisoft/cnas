using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="PasswordPolicyValidator"/> — the FluentValidation rule-set
/// enforcing the SEC 014 / R0052 password policy on local-login credentials.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the validator. Policy under test:
/// <list type="bullet">
///   <item>Min length 8, max length 128.</item>
///   <item>At least one lowercase, one uppercase, one digit, one symbol.</item>
/// </list>
/// Every rejection asserts the <see cref="ErrorCodes.PasswordPolicyViolation"/> error
/// code surfaces so callers can branch on the stable identifier (CLAUDE.md §2.2).
/// </remarks>
public sealed class PasswordPolicyValidatorTests
{
    /// <summary>Single validator instance reused across tests — validators are stateless.</summary>
    private readonly PasswordPolicyValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Plaintext_NullOrEmpty_Fails(string? plaintext)
    {
        // Null/empty plaintext violates the not-empty + min-length rules.
        var input = new PasswordInput(plaintext!);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_TooShort_Fails()
    {
        // 6 characters with a symbol and digit — fails the min-length rule.
        var input = new PasswordInput("short!");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_NoUppercase_Fails()
    {
        // Has length, lowercase, digit, symbol — missing only the uppercase letter.
        var input = new PasswordInput("alllowercase1!");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_NoLowercase_Fails()
    {
        // Has length, uppercase, digit, symbol — missing only the lowercase letter.
        var input = new PasswordInput("ALLUPPERCASE1!");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_NoDigit_Fails()
    {
        // Has length, mixed case, symbol — missing only the digit.
        var input = new PasswordInput("NoDigits!Yes");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_NoSymbol_Fails()
    {
        // Has length, mixed case, digit — missing only the symbol.
        var input = new PasswordInput("NoSymbols1Yes");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }

    [Fact]
    public void Plaintext_MeetsAllRulesAtMinimumLength_Passes()
    {
        // 8 characters exactly with one of each required character class.
        var input = new PasswordInput("Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Plaintext_AtMaximumLength_Passes()
    {
        // 128 characters — exactly at the ceiling. Compose a compliant string by padding
        // the canonical "Aa1!" prefix with lowercase letters up to 128 chars total.
        var input = new PasswordInput("Aa1!" + new string('a', 124));
        input.Plaintext.Length.Should().Be(128);

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Plaintext_OneOverMaximumLength_Fails()
    {
        // 129 characters — one above the 128 ceiling.
        var input = new PasswordInput("Aa1!" + new string('a', 125));
        input.Plaintext.Length.Should().Be(129);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Plaintext)
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation);
    }
}
