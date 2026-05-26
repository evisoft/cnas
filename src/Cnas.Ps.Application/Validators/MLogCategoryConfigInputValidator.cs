using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — validates a
/// <see cref="MLogCategoryConfigInputDto"/> before it crosses the API
/// boundary into the MLog category-filter service.
/// </summary>
public sealed partial class MLogCategoryConfigInputValidator
    : AbstractValidator<MLogCategoryConfigInputDto>
{
    /// <summary>Regex pinning the SCREAMING_SNAKE_CASE category-code shape.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$")]
    private static partial Regex CategoryCodeRegex();

    /// <summary>Builds the rule set.</summary>
    public MLogCategoryConfigInputValidator()
    {
        RuleFor(x => x.CategoryCode)
            .NotEmpty()
            .WithMessage("CategoryCode is required.")
            .Matches(CategoryCodeRegex())
            .WithMessage("CategoryCode must match SCREAMING_SNAKE_CASE (e.g. AUTH, APPLICATION.RECEIVE).");

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(MLogCategoryConfig.MaxDisplayNameLength)
            .WithMessage($"DisplayName cannot exceed {MLogCategoryConfig.MaxDisplayNameLength} characters.");

        RuleFor(x => x.MinSeverity)
            .IsInEnum()
            .WithMessage("MinSeverity must be Notice or Critical.");
    }
}
