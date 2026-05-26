using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>Validates the self-service profile update form (UC13).</summary>
public sealed class ProfileUpdateInputValidator : AbstractValidator<ProfileUpdateInput>
{
    private static readonly string[] AllowedLanguages = ["ro", "en", "ru"];

    /// <summary>Creates the validator.</summary>
    public ProfileUpdateInputValidator()
    {
        RuleFor(x => x.Email)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Email must be valid.");

        RuleFor(x => x.Phone)
            .Matches(@"^\+?[0-9 ()\-]{6,20}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("Phone must be a valid E.164/local number.");

        RuleFor(x => x.PreferredLanguage)
            .NotEmpty()
            .Must(lang => AllowedLanguages.Contains(lang))
            .WithMessage("Preferred language must be one of: ro, en, ru.");
    }
}
