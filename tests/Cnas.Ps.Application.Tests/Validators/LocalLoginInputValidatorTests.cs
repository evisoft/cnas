using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="LocalLoginInputValidator"/> — the FluentValidation rule
/// set guarding the R0051 local-login entry point. Per CLAUDE.md RULE 1 the tests
/// were drafted before the validator code; they assert the stable
/// <see cref="ErrorCodes.LoginInvalid"/> surface plus per-rule length / shape gates.
/// </summary>
/// <remarks>
/// The validator is intentionally minimal — login authentication accepts historical
/// weak passwords so a user can sign in to rotate them. Composition policy lives
/// on the change-password surface (<see cref="PasswordPolicyValidator"/>), not here.
/// </remarks>
public sealed class LocalLoginInputValidatorTests
{
    /// <summary>Single validator instance reused across tests — validators are stateless.</summary>
    private readonly LocalLoginInputValidator _validator = new();

    // ─────────────────────── Login field ───────────────────────

    [Theory]
    [InlineData(null, "Aa1!aaaa")]
    [InlineData("", "Aa1!aaaa")]
    public void Login_NullOrEmpty_Fails(string? login, string password)
    {
        var input = new LocalLoginInputDto(login!, password);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Login)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Login_TooShort_Fails()
    {
        // 2 chars — under the minimum length of 3.
        var input = new LocalLoginInputDto("ab", "Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Login)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Login_TooLong_Fails()
    {
        // 65 chars — one above the 64-char ceiling.
        var input = new LocalLoginInputDto(new string('a', 65), "Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Login)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Login_InvalidChars_Fails()
    {
        // Whitespace breaks the [a-zA-Z0-9._-]+ regex.
        var input = new LocalLoginInputDto("bad login", "Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Login)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Login_ValidShape_Passes()
    {
        // Exact mix of allowed characters at length 12.
        var input = new LocalLoginInputDto("user.name_42", "Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveValidationErrorFor(x => x.Login);
    }

    // ─────────────────────── Password field ───────────────────────

    [Theory]
    [InlineData("validuser", null)]
    [InlineData("validuser", "")]
    public void Password_NullOrEmpty_Fails(string login, string? password)
    {
        var input = new LocalLoginInputDto(login, password!);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Password_TooShort_Fails()
    {
        // 7 chars — one under the minimum of 8.
        var input = new LocalLoginInputDto("validuser", "Aa1!aaa");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Password_TooLong_Fails()
    {
        // 257 chars — one above the 256 ceiling.
        var input = new LocalLoginInputDto("validuser", new string('a', 257));

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorCode(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public void Password_LegacyWeakInput_PassesLoginValidator()
    {
        // The login validator deliberately accepts weak passwords — only length is
        // gated. The composition policy lives on the change-password surface.
        var input = new LocalLoginInputDto("validuser", "abcdefgh");

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void HappyPath_PassesValidator()
    {
        var input = new LocalLoginInputDto("user.name_42", "Aa1!aaaa");

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
