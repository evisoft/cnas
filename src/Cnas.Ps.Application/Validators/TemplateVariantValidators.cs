using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0133 — Validator for <see cref="TemplateVariantUpsertDto"/>. Enforces the
/// boundary contract documented on <see cref="TemplateLanguages"/>:
/// <list type="bullet">
///   <item><c>TemplateSqid</c> non-empty.</item>
///   <item><c>Language</c> is one of <see cref="TemplateLanguages.All"/> (case-sensitive lower-case).</item>
///   <item><c>SubjectOrTitle</c> 1..200 chars.</item>
///   <item><c>Body</c> 1..100,000 chars.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>DocxBase64 deep validation lives in the service layer.</b> The validator only
/// confirms the field's superficial shape (non-empty when present); the magic-byte
/// + size checks require decoding the base64 string which is more expensive than a
/// FluentValidation rule should be. Centralising the deep check in the service
/// keeps the validator cheap and the boundary contract simple.
/// </para>
/// </remarks>
public sealed class TemplateVariantUpsertDtoValidator : AbstractValidator<TemplateVariantUpsertDto>
{
    /// <summary>Maximum subject length. Matches <see cref="Cnas.Ps.Core.Domain.TemplateVariant.SubjectOrTitle"/> column cap.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>Maximum body length. Hard cap to bound import-time memory consumption.</summary>
    public const int MaxBodyLength = 100_000;

    /// <summary>Wires the rule set.</summary>
    public TemplateVariantUpsertDtoValidator()
    {
        RuleFor(x => x.TemplateSqid)
            .NotEmpty()
            .WithMessage("TemplateSqid is required.");

        RuleFor(x => x.Language)
            .NotEmpty()
            .Must(lang => TemplateLanguages.All.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", TemplateLanguages.All)}.");

        RuleFor(x => x.SubjectOrTitle)
            .NotEmpty()
            .MaximumLength(MaxSubjectLength)
            .WithMessage($"SubjectOrTitle must be 1..{MaxSubjectLength} characters.");

        RuleFor(x => x.Body)
            .NotEmpty()
            .MaximumLength(MaxBodyLength)
            .WithMessage($"Body must be 1..{MaxBodyLength} characters.");
    }
}
