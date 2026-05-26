using Cnas.Ps.Application.Localization;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0225 / TOR UI 015 — validator for <see cref="HelpTopicUpsertDto"/>. Reuses the
/// translation-key code regex via
/// <see cref="TranslationKeyUpsertDtoValidator.CodeIsValid"/> so the two registries
/// share one naming convention.
/// </summary>
public sealed class HelpTopicUpsertDtoValidator : AbstractValidator<HelpTopicUpsertDto>
{
    /// <summary>Creates the validator with the full rule set.</summary>
    public HelpTopicUpsertDtoValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Must(TranslationKeyUpsertDtoValidator.CodeIsValid)
            .WithMessage("Code must match " + TranslationKeyUpsertDtoValidator.CodePattern + ".");

        RuleFor(x => x.Module)
            .NotEmpty().WithMessage("Module is required.")
            .MaximumLength(64).WithMessage("Module exceeds the 64-character cap.");

        RuleFor(x => x.AnchorSelector)
            .MaximumLength(256).WithMessage("AnchorSelector exceeds the 256-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.AnchorSelector));
    }
}

/// <summary>
/// R0225 / TOR UI 015 — validator for <see cref="HelpTopicTranslationUpsertDto"/>.
/// Enforces the title cap (1..200) and the body cap (1..20_000) so contextual help
/// stays "tip-shaped" rather than turning into a documentation portal in disguise.
/// </summary>
public sealed class HelpTopicTranslationUpsertDtoValidator
    : AbstractValidator<HelpTopicTranslationUpsertDto>
{
    /// <summary>Creates the validator with the full rule set.</summary>
    public HelpTopicTranslationUpsertDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title exceeds the 200-character cap.");

        RuleFor(x => x.BodyMarkdown)
            .NotEmpty().WithMessage("BodyMarkdown is required.")
            .MaximumLength(20_000).WithMessage("BodyMarkdown exceeds the 20000-character cap.");

        RuleFor(x => x.TranslatorNote)
            .MaximumLength(1024).WithMessage("TranslatorNote exceeds the 1024-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.TranslatorNote));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="language"/> is one of the canonical
    /// CNAS language codes (<see cref="TranslationLanguages.All"/>).
    /// </summary>
    /// <param name="language">Caller-supplied language code from the route.</param>
    /// <returns><c>true</c> when supported.</returns>
    public static bool LanguageIsSupported(string? language) =>
        TranslationLanguages.IsSupported(language);
}
