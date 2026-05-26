using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// Validates <see cref="InsuredPersonRegistrationInput"/> at the API boundary per CLAUDE.md §2.5.
/// IDNP is checked via the <see cref="Idnp"/> value object (format + mod-10 checksum); the
/// name fields are length-bounded and must contain at least one letter so that purely
/// numeric / punctuation names are rejected.
/// </summary>
/// <remarks>
/// FluentValidation's ASP.NET integration constructs validators via a parameterless
/// constructor, so the dependency on the system clock — needed for the "BirthDate is in
/// the past" rule — is exposed via a settable static <see cref="Clock"/> property whose
/// default is a fresh <see cref="SystemTimeProvider"/>. Tests override this property to
/// pin a deterministic "today" without forcing the production registration to thread the
/// clock through the constructor. The trade-off is documented here so the choice is not
/// silently inherited: a constructor-based abstraction would be cleaner, but it would
/// also require touching the DI registration of every validator and is out of scope for
/// the Annex 2 MVP.
/// </remarks>
/// <example>
/// <code>
/// var validator = new InsuredPersonRegistrationInputValidator();
/// var result = validator.Validate(new InsuredPersonRegistrationInput(
///     "2000123456782", "Popescu", "Ion", null, new DateOnly(1980, 5, 12)));
/// // result.IsValid → true (assuming the checksum digit is correct)
/// </code>
/// </example>
public sealed class InsuredPersonRegistrationInputValidator : AbstractValidator<InsuredPersonRegistrationInput>
{
    /// <summary>Maximum permitted length for each of the three name components (DB column constraint).</summary>
    private const int NameMaxLength = 100;

    /// <summary>
    /// Regex that requires the candidate string to contain at least one Unicode letter
    /// anywhere — guarding against pathological inputs that pass NotEmpty/MaxLength but
    /// contain only digits, whitespace, or punctuation (e.g. "1234", "---").
    /// </summary>
    private static readonly Regex AtLeastOneLetter =
        new(@"^.*\p{L}.*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Clock used to evaluate the "BirthDate must be in the past" rule. Defaults to the
    /// real system clock; tests assign a stub instance to make "today" deterministic.
    /// Marked <see langword="internal"/> so only same-assembly tests (via InternalsVisibleTo
    /// where configured) and the production composition root can swap it.
    /// </summary>
    internal static ICnasTimeProvider Clock { get; set; } = new SystemTimeProvider();

    /// <summary>Creates the validator with all field rules in place.</summary>
    public InsuredPersonRegistrationInputValidator()
    {
        RuleFor(x => x.Idnp)
            .NotEmpty()
            .WithMessage("IDNP is required.")
            .Must(BeValidIdnp)
            .WithMessage("IDNP must be 13 digits, start with 0/1/2, and satisfy the mod-10 checksum.");

        // NOTE: in FluentValidation, a trailing .When(...) applies to the ENTIRE preceding
        // rule chain, not just the immediately-prior validator. Putting the "at least one
        // letter" guard inside the same chain as NotEmpty() would therefore suppress the
        // NotEmpty() error when the value is empty — exactly the opposite of intent. The
        // letter guard is therefore declared as a SECOND RuleFor whose own When() short-
        // circuits when the value is null/empty (NotEmpty() will already have surfaced).
        RuleFor(x => x.LastName)
            .NotEmpty()
            .WithMessage("LastName is required.")
            .MaximumLength(NameMaxLength)
            .WithMessage($"LastName cannot exceed {NameMaxLength} characters.");

        RuleFor(x => x.LastName)
            .Must(ContainsAtLeastOneLetter!)
            .When(x => !string.IsNullOrEmpty(x.LastName))
            .WithMessage("LastName must contain at least one letter.");

        RuleFor(x => x.FirstName)
            .NotEmpty()
            .WithMessage("FirstName is required.")
            .MaximumLength(NameMaxLength)
            .WithMessage($"FirstName cannot exceed {NameMaxLength} characters.");

        RuleFor(x => x.FirstName)
            .Must(ContainsAtLeastOneLetter!)
            .When(x => !string.IsNullOrEmpty(x.FirstName))
            .WithMessage("FirstName must contain at least one letter.");

        RuleFor(x => x.Patronymic)
            .MaximumLength(NameMaxLength)
            .When(x => x.Patronymic is not null)
            .WithMessage($"Patronymic cannot exceed {NameMaxLength} characters.");

        RuleFor(x => x.BirthDate)
            .Must(BeInThePast)
            .WithMessage("BirthDate must be in the past.");
    }

    /// <summary>
    /// Bridges FluentValidation's <c>Must(...)</c> predicate to the <see cref="Idnp"/>
    /// value-object's structural validation (format + mod-10 checksum).
    /// </summary>
    /// <param name="candidate">Raw input string (may be null or whitespace).</param>
    /// <returns>True when the input would produce a successful <see cref="Idnp"/>.</returns>
    private static bool BeValidIdnp(string? candidate) => Idnp.TryCreate(candidate).IsSuccess;

    /// <summary>True when <paramref name="value"/> contains at least one Unicode letter.</summary>
    /// <param name="value">Candidate name component.</param>
    /// <returns>True if at least one Unicode letter is present.</returns>
    private static bool ContainsAtLeastOneLetter(string value) =>
        AtLeastOneLetter.IsMatch(value);

    /// <summary>True when <paramref name="date"/> is strictly earlier than the clock's "today".</summary>
    /// <param name="date">Candidate birth date.</param>
    /// <returns>True when the birth date is in the past relative to the injected clock.</returns>
    private static bool BeInThePast(DateOnly date) => date < Clock.TodayUtc;
}
