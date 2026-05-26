using System.Text.RegularExpressions;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0210 / TOR UI 007 — validator for <see cref="TranslationKeyUpsertDto"/>. Enforces
/// the kebab-case code shape, length caps on description / module, and the module
/// vocabulary constraint (free-form but capped).
/// </summary>
/// <remarks>
/// <b>Code regex.</b> <c>^[a-z][a-z0-9.-]{1,127}$</c> — must start with a lowercase
/// letter, body characters are lowercase letters / digits / dot / hyphen, total 2..128
/// characters. The leading-letter rule prevents codes like <c>"123.foo"</c> that would
/// surprise the operator searching by prefix; the dot is allowed so codes can carry a
/// page-section hierarchy.
/// </remarks>
public sealed class TranslationKeyUpsertDtoValidator : AbstractValidator<TranslationKeyUpsertDto>
{
    /// <summary>Anchored regex enforcing the kebab-case code shape.</summary>
    internal const string CodePattern = "^[a-z][a-z0-9.-]{1,127}$";

    private static readonly Regex CodeRegex = new(
        CodePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Creates the validator with the full rule set.</summary>
    public TranslationKeyUpsertDtoValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Must(CodeIsValid)
            .WithMessage("Code must match " + CodePattern + ".");

        RuleFor(x => x.Description)
            .MaximumLength(1024).WithMessage("Description exceeds the 1024-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleFor(x => x.Module)
            .MaximumLength(64).WithMessage("Module exceeds the 64-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Module));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> matches the canonical
    /// translation-key code shape. Anchored regex match — substring matches are
    /// rejected. Exposed as <c>internal static</c> so callers (validator for the
    /// help registry, etc.) can reuse the rule without duplicating the regex.
    /// </summary>
    /// <param name="value">Caller-supplied code value.</param>
    /// <returns><c>true</c> when the code is canonical.</returns>
    internal static bool CodeIsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            return CodeRegex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// R0210 / TOR UI 007 — validator for <see cref="TranslationValueUpsertDto"/>.
/// Enforces the text length (1..2000) and the optional translator-note cap. The
/// language is validated as a route segment, not as part of the body.
/// </summary>
public sealed class TranslationValueUpsertDtoValidator : AbstractValidator<TranslationValueUpsertDto>
{
    /// <summary>Creates the validator with the full rule set.</summary>
    public TranslationValueUpsertDtoValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage("Text is required.")
            .MaximumLength(2000).WithMessage("Text exceeds the 2000-character cap.");

        RuleFor(x => x.TranslatorNote)
            .MaximumLength(1024).WithMessage("TranslatorNote exceeds the 1024-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.TranslatorNote));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="language"/> is one of the canonical
    /// CNAS language codes (<see cref="TranslationLanguages.All"/>). Exposed as a
    /// static helper so the service layer can reject an unknown route segment before
    /// invoking the body validator.
    /// </summary>
    /// <param name="language">Caller-supplied language code from the route.</param>
    /// <returns><c>true</c> when the code is on the allow-list.</returns>
    public static bool LanguageIsSupported(string? language) =>
        TranslationLanguages.IsSupported(language);
}
