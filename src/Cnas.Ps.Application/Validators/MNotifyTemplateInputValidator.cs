using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0115 / TOR CF 14.07 — validates an <see cref="MNotifyTemplateInputDto"/>
/// before it crosses the API boundary into the template-registry service.
/// </summary>
/// <remarks>
/// Constraints:
/// <list type="bullet">
///   <item><c>Code</c> matches <c>^[A-Z][A-Z0-9_.]{1,79}$</c> — SCREAMING_SNAKE_CASE
///   with optional dotted namespace segments.</item>
///   <item><c>ChannelKind</c> is a known enum value.</item>
///   <item><c>Subject</c> is required when <c>ChannelKind == Email</c>; capped at 256 chars.</item>
///   <item><c>BodyMarkdown</c> is non-empty and ≤ 16 KiB.</item>
/// </list>
/// </remarks>
public sealed partial class MNotifyTemplateInputValidator : AbstractValidator<MNotifyTemplateInputDto>
{
    /// <summary>Regex pinning the SCREAMING_SNAKE_CASE code shape (with dotted segments).</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,79}$")]
    private static partial Regex CodeRegex();

    /// <summary>Builds the rule set.</summary>
    public MNotifyTemplateInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .WithMessage("Code is required.")
            .Matches(CodeRegex())
            .WithMessage("Code must match SCREAMING_SNAKE_CASE pattern (e.g. WORKFLOW.TASK.ASSIGNED).");

        RuleFor(x => x.ChannelKind)
            .IsInEnum()
            .WithMessage("ChannelKind must be Email, Sms, Viber or Push.");

        RuleFor(x => x.Subject)
            .NotEmpty()
            .When(x => x.ChannelKind == MNotifyChannelKindDto.Email)
            .WithMessage("Subject is required for Email templates.")
            .MaximumLength(MNotifyTemplate.MaxSubjectLength)
            .WithMessage($"Subject cannot exceed {MNotifyTemplate.MaxSubjectLength} characters.");

        RuleFor(x => x.BodyMarkdown)
            .NotEmpty()
            .WithMessage("BodyMarkdown is required.")
            .MaximumLength(MNotifyTemplate.MaxBodyLength)
            .WithMessage($"BodyMarkdown cannot exceed {MNotifyTemplate.MaxBodyLength} characters.");
    }
}
