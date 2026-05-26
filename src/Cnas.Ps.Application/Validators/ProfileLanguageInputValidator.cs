using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0211 / TOR UI 003 — validates the thin <see cref="ProfileLanguageInputDto"/>
/// shape accepted by <c>PUT /api/profile/language</c>. Only one rule applies: the
/// supplied language must be in the allow-list <c>ro</c> / <c>en</c> / <c>ru</c>
/// (case-insensitive at the boundary; the service normalises to lowercase).
/// </summary>
public sealed class ProfileLanguageInputValidator : AbstractValidator<ProfileLanguageInputDto>
{
    /// <summary>Frozen allow-list of supported UI languages.</summary>
    public static readonly string[] AllowedLanguages = ["ro", "en", "ru"];

    /// <summary>Wires the single language allow-list rule at construction time.</summary>
    public ProfileLanguageInputValidator()
    {
        RuleFor(x => x.Language)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Language is required.")
            .Must(IsAllowedLanguage)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Language must be one of: ro, en, ru.");
    }

    /// <summary>Returns true when the supplied code matches an entry in the allow-list (case-insensitive).</summary>
    /// <param name="language">Candidate ISO language code.</param>
    /// <returns>Whether the code is allowed.</returns>
    private static bool IsAllowedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }
        foreach (var allowed in AllowedLanguages)
        {
            if (string.Equals(language, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
