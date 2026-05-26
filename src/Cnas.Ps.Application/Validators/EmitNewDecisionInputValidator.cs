using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0573 / TOR CF 08.05 — FluentValidation rules for
/// <see cref="EmitNewDecisionInputDto"/>. Enforces the three caps documented on
/// the DTO: a non-empty 3-64 char decision template code, an optional ≤1000
/// char note, and an optional strictly-positive override amount.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why kebab-case isn't enforced here.</b> The template-code character set is
/// intentionally permissive at the boundary — the downstream service performs
/// the case-insensitive lookup against the registered
/// <c>IDocxTemplate.TemplateCode</c> values, and the lookup miss surfaces a
/// dedicated <c>DOCUMENT.TEMPLATE_NOT_FOUND</c> code rather than a generic
/// validation failure. Rejecting "DECIZIA_PENSIE" at the validator would force
/// the UI to lowercase the value before submitting, which is fragile.
/// </para>
/// <para>
/// <b>Override amount semantics.</b> Negative or zero overrides are operator
/// errors (the examiner is meant to grant a benefit, not retract one through
/// an override). Surfacing those at the validator keeps the service body
/// focused on business-state guards (editable status, template existence,
/// notification side-effects).
/// </para>
/// </remarks>
public sealed class EmitNewDecisionInputValidator : AbstractValidator<EmitNewDecisionInputDto>
{
    /// <summary>Minimum permitted length of <c>DecisionTemplateCode</c>: 3 chars.</summary>
    public const int MinTemplateCodeLength = 3;

    /// <summary>Maximum permitted length of <c>DecisionTemplateCode</c>: 64 chars.</summary>
    public const int MaxTemplateCodeLength = 64;

    /// <summary>Maximum permitted character length of <c>Notes</c>: 1000 chars.</summary>
    public const int MaxNotesLength = 1000;

    /// <summary>Creates the validator with the full rule set.</summary>
    public EmitNewDecisionInputValidator()
    {
        RuleFor(x => x.DecisionTemplateCode)
            .NotEmpty()
            .WithMessage("DecisionTemplateCode is required.");

        RuleFor(x => x.DecisionTemplateCode)
            .MinimumLength(MinTemplateCodeLength)
            .WithMessage($"DecisionTemplateCode must be at least {MinTemplateCodeLength} characters.")
            .MaximumLength(MaxTemplateCodeLength)
            .WithMessage($"DecisionTemplateCode must be at most {MaxTemplateCodeLength} characters.")
            .When(x => !string.IsNullOrEmpty(x.DecisionTemplateCode));

        RuleFor(x => x.Notes)
            .MaximumLength(MaxNotesLength)
            .WithMessage($"Notes exceeds the {MaxNotesLength}-character cap.")
            .When(x => !string.IsNullOrEmpty(x.Notes));

        RuleFor(x => x.OverrideAmount)
            .GreaterThan(0m)
            .WithMessage("OverrideAmount must be greater than zero when supplied.")
            .When(x => x.OverrideAmount.HasValue);
    }
}
