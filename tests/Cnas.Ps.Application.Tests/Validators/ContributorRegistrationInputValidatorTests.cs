using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="ContributorRegistrationInputValidator"/> covering every
/// per-field rule plus a positive happy-path baseline (CLAUDE.md §3.3 — unit tier).
/// </summary>
public sealed class ContributorRegistrationInputValidatorTests
{
    /// <summary>Single validator instance reused across tests (validators are stateless).</summary>
    private readonly ContributorRegistrationInputValidator _validator = new();

    /// <summary>
    /// A canonical valid IDNO. Generated to satisfy the mod-10 checksum: weights are
    /// {7,3,1} cycling over the first twelve digits. For "100360001234" the weighted sum
    /// is 7+0+0+21+18+0+0+0+1+14+9+4 = 74; (10 - 74%10)%10 = 6, so the full IDNO is
    /// "1003600012346".
    /// </summary>
    private const string ValidIdno = "1003600012346";

    /// <summary>Builds a known-good input that callers can mutate per test.</summary>
    /// <param name="idno">Optional IDNO override.</param>
    /// <param name="denumire">Optional Denumire override.</param>
    /// <param name="cfojCode">Optional CFOJ override.</param>
    /// <param name="caemCode">Optional CAEM override.</param>
    private static ContributorRegistrationInput BuildValid(
        string idno = ValidIdno,
        string denumire = "SRL Exemplu",
        string? cfojCode = "1170",
        string? caemCode = "47111")
        => new(idno, denumire, cfojCode, caemCode);

    [Fact]
    public void Valid_Input_PassesAllRules()
    {
        // Arrange: every field at a known-good value.
        var input = BuildValid();

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Idno_Empty_Fails()
    {
        // Arrange: empty IDNO — fails NotEmpty AND Idno.TryCreate.
        var input = BuildValid(idno: string.Empty);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Idno);
    }

    [Fact]
    public void Idno_BadChecksum_Fails()
    {
        // Arrange: 13 digits in the right shape but the last digit deliberately wrong.
        var bad = ValidIdno[..12] + (ValidIdno[12] == '0' ? '1' : '0');
        var input = BuildValid(idno: bad);

        // Act
        var result = _validator.TestValidate(input);

        // Assert: Must() returns false → BeValidIdno rule fires.
        result.ShouldHaveValidationErrorFor(x => x.Idno);
    }

    [Fact]
    public void Idno_WrongLength_Fails()
    {
        // Arrange: 12 digits — fails the [1-9][0-9]{12} pattern.
        var input = BuildValid(idno: "100360001234");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Idno);
    }

    [Fact]
    public void Idno_StartsWithZero_Fails()
    {
        // Arrange: leading zero is reserved for natural persons (IDNP), not IDNO.
        var input = BuildValid(idno: "0003600012342");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Idno);
    }

    [Fact]
    public void Denumire_Empty_Fails()
    {
        // Arrange
        var input = BuildValid(denumire: string.Empty);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Denumire);
    }

    [Fact]
    public void Denumire_TooLong_Fails()
    {
        // Arrange: 257 chars — one above the 256 ceiling.
        var input = BuildValid(denumire: new string('a', 257));

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Denumire);
    }

    [Fact]
    public void CfojCode_Null_Passes()
    {
        // Arrange: optional field; null is allowed and must not trigger the regex rule.
        var input = BuildValid(cfojCode: null);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.CfojCode);
    }

    [Fact]
    public void CfojCode_InvalidFormat_Fails()
    {
        // Arrange: 5 digits (over the 4-digit ceiling).
        var input = BuildValid(cfojCode: "11700");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CfojCode);
    }

    [Fact]
    public void CfojCode_NonDigits_Fails()
    {
        // Arrange: letters are not permitted.
        var input = BuildValid(cfojCode: "11AB");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CfojCode);
    }

    [Fact]
    public void CaemCode_InvalidFormat_Fails()
    {
        // Arrange: 6 digits (over the 5-digit ceiling).
        var input = BuildValid(caemCode: "471110");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CaemCode);
    }

    [Fact]
    public void CaemCode_Null_Passes()
    {
        // Arrange: optional field; null is allowed.
        var input = BuildValid(caemCode: null);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.CaemCode);
    }
}
