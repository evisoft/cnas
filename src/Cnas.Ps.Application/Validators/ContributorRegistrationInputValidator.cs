using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.ValueObjects;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// Validates <see cref="ContributorRegistrationInput"/> at the API boundary per CLAUDE.md §2.5.
/// IDNO is checked via the <see cref="Idno"/> value object (format + mod-10 checksum); CFOJ
/// and CAEM codes are validated against the digit-count regexes published in the TOR
/// classificator annex.
/// </summary>
/// <example>
/// <code>
/// var validator = new ContributorRegistrationInputValidator();
/// var result = validator.Validate(new ContributorRegistrationInput("1003600012345", "SRL X", null, null));
/// // result.IsValid → true (assuming the checksum digit is correct)
/// </code>
/// </example>
public sealed class ContributorRegistrationInputValidator : AbstractValidator<ContributorRegistrationInput>
{
    /// <summary>CFOJ — Clasificatorul Formelor de Organizare Juridică — 1 to 4 numeric digits.</summary>
    private static readonly Regex CfojPattern =
        new("^[0-9]{1,4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>CAEM — Clasificatorul Activităţilor din Economia Moldovei — 1 to 5 numeric digits.</summary>
    private static readonly Regex CaemPattern =
        new("^[0-9]{1,5}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Maximum permitted display name length (DB column constraint).</summary>
    private const int DenumireMaxLength = 256;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public ContributorRegistrationInputValidator()
    {
        RuleFor(x => x.Idno)
            .NotEmpty()
            .WithMessage("IDNO is required.")
            .Must(BeValidIdno)
            .WithMessage("IDNO must be 13 digits, start with 1-9, and satisfy the mod-10 checksum.");

        RuleFor(x => x.Denumire)
            .NotEmpty()
            .WithMessage("Denumire is required.")
            .MaximumLength(DenumireMaxLength)
            .WithMessage($"Denumire cannot exceed {DenumireMaxLength} characters.");

        // Optional classifier codes — only validate the format when supplied.
        RuleFor(x => x.CfojCode)
            .Must(value => value is null || CfojPattern.IsMatch(value))
            .WithMessage("CfojCode must be 1-4 numeric digits when supplied.");

        RuleFor(x => x.CaemCode)
            .Must(value => value is null || CaemPattern.IsMatch(value))
            .WithMessage("CaemCode must be 1-5 numeric digits when supplied.");
    }

    /// <summary>
    /// Bridges FluentValidation's <c>Must(...)</c> predicate to the <see cref="Idno"/>
    /// value-object's structural validation (format + mod-10 checksum).
    /// </summary>
    /// <param name="candidate">Raw input string (may be null or whitespace).</param>
    /// <returns>True when the input would produce a successful <see cref="Idno"/>.</returns>
    private static bool BeValidIdno(string? candidate) => Idno.TryCreate(candidate).IsSuccess;
}
