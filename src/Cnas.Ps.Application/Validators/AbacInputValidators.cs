using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2271 / TOR SEC 025 — shared constants + helpers for the ABAC validators.
/// Centralised so the regex / size bounds do not drift across the create /
/// modify / filter rule sets.
/// </summary>
internal static partial class AbacValidatorShared
{
    /// <summary>Minimum reason / display-name length.</summary>
    public const int MinDisplayNameLength = 3;

    /// <summary>Maximum display-name length.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>Maximum description length on rule sets.</summary>
    public const int MaxRuleSetDescriptionLength = 1000;

    /// <summary>Maximum description length on individual rules.</summary>
    public const int MaxRuleDescriptionLength = 500;

    /// <summary>Maximum condition-expression length.</summary>
    public const int MaxConditionExpressionLength = 2048;

    /// <summary>Minimum order-index value.</summary>
    public const int MinOrderIndex = 0;

    /// <summary>Maximum order-index value — wide enough for any realistic policy.</summary>
    public const int MaxOrderIndex = 10000;

    /// <summary>Minimum reason length on disable / enable envelopes.</summary>
    public const int MinReasonLength = 3;

    /// <summary>Maximum reason length on disable / enable envelopes.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Maximum page size accepted by the list endpoint.</summary>
    public const int MaxTake = 100;

    /// <summary>Maximum number of keys accepted in a single dry-run attribute dictionary.</summary>
    public const int MaxAttributeKeys = 64;

    /// <summary>Policy-name regex — SCREAMING_SNAKE_CASE (with dots/digits/underscore), 2..64 chars.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex PolicyNameRegex();

    /// <summary>True when <paramref name="policyName"/> matches the canonical SCREAMING_SNAKE_CASE shape.</summary>
    /// <param name="policyName">Candidate policy name.</param>
    /// <returns><c>true</c> when shape-conformant; <c>false</c> when null/empty or malformed.</returns>
    public static bool PolicyNameIsValid(string? policyName)
        => !string.IsNullOrWhiteSpace(policyName) && PolicyNameRegex().IsMatch(policyName);

    /// <summary>True when <paramref name="policyName"/> is null OR matches the canonical shape.</summary>
    /// <param name="policyName">Candidate policy name.</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool PolicyNameIsValidOrNull(string? policyName)
        => policyName is null || PolicyNameRegex().IsMatch(policyName);

    /// <summary>True when <paramref name="effect"/> is null OR parses to a valid <see cref="AbacEffect"/>.</summary>
    /// <param name="effect">Candidate effect name.</param>
    /// <returns><c>true</c> when null or a valid enum name.</returns>
    public static bool EffectIsValidOrNull(string? effect)
        => effect is null || Enum.TryParse<AbacEffect>(effect, ignoreCase: false, out _);

    /// <summary>True when <paramref name="effect"/> is non-null and parses to a valid <see cref="AbacEffect"/>.</summary>
    /// <param name="effect">Candidate effect name.</param>
    /// <returns><c>true</c> when present and shape-conformant; otherwise <c>false</c>.</returns>
    public static bool EffectIsValid(string? effect)
        => effect is not null && Enum.TryParse<AbacEffect>(effect, ignoreCase: false, out _);
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacRuleSetCreateInputDto"/>.
/// </summary>
public sealed class AbacRuleSetCreateInputValidator : AbstractValidator<AbacRuleSetCreateInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacRuleSetCreateInputValidator()
    {
        RuleFor(x => x.PolicyName)
            .Must(AbacValidatorShared.PolicyNameIsValid)
            .WithMessage("PolicyName must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(AbacValidatorShared.MinDisplayNameLength)
            .WithMessage($"DisplayName must be at least {AbacValidatorShared.MinDisplayNameLength} characters.")
            .MaximumLength(AbacValidatorShared.MaxDisplayNameLength)
            .WithMessage($"DisplayName cannot exceed {AbacValidatorShared.MaxDisplayNameLength} characters.");

        RuleFor(x => x.Description)
            .MaximumLength(AbacValidatorShared.MaxRuleSetDescriptionLength)
            .When(x => x.Description is not null)
            .WithMessage($"Description cannot exceed {AbacValidatorShared.MaxRuleSetDescriptionLength} characters.");

        RuleFor(x => x.DefaultEffect)
            .Must(AbacValidatorShared.EffectIsValidOrNull)
            .WithMessage("DefaultEffect must be one of the AbacEffect enum names (Allow / Deny) when supplied.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacRuleSetModifyInputDto"/>.
/// </summary>
public sealed class AbacRuleSetModifyInputValidator : AbstractValidator<AbacRuleSetModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacRuleSetModifyInputValidator()
    {
        RuleFor(x => x.DisplayName)
            .MinimumLength(AbacValidatorShared.MinDisplayNameLength)
            .When(x => x.DisplayName is not null)
            .WithMessage($"DisplayName must be at least {AbacValidatorShared.MinDisplayNameLength} characters when supplied.")
            .MaximumLength(AbacValidatorShared.MaxDisplayNameLength)
            .When(x => x.DisplayName is not null)
            .WithMessage($"DisplayName cannot exceed {AbacValidatorShared.MaxDisplayNameLength} characters.");

        RuleFor(x => x.Description)
            .MaximumLength(AbacValidatorShared.MaxRuleSetDescriptionLength)
            .When(x => x.Description is not null)
            .WithMessage($"Description cannot exceed {AbacValidatorShared.MaxRuleSetDescriptionLength} characters.");

        RuleFor(x => x.DefaultEffect)
            .Must(AbacValidatorShared.EffectIsValidOrNull)
            .WithMessage("DefaultEffect must be one of the AbacEffect enum names (Allow / Deny) when supplied.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(AbacValidatorShared.MinReasonLength)
            .WithMessage($"ChangeReason must be at least {AbacValidatorShared.MinReasonLength} characters.")
            .MaximumLength(AbacValidatorShared.MaxReasonLength)
            .WithMessage($"ChangeReason cannot exceed {AbacValidatorShared.MaxReasonLength} characters.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacRuleInputDto"/>. Parses the
/// condition expression through the injected
/// <see cref="IAbacExpressionParser"/> and FAILS the validator when parsing
/// fails, so a malformed rule never reaches the service layer.
/// </summary>
public sealed class AbacRuleInputValidator : AbstractValidator<AbacRuleInputDto>
{
    /// <summary>Builds the rule set with the parser injected for expression validation.</summary>
    /// <param name="parser">The shared ABAC expression parser used to pre-check expressions.</param>
    public AbacRuleInputValidator(IAbacExpressionParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);

        RuleFor(x => x.OrderIndex)
            .InclusiveBetween(AbacValidatorShared.MinOrderIndex, AbacValidatorShared.MaxOrderIndex)
            .WithMessage($"OrderIndex must be in {AbacValidatorShared.MinOrderIndex}..{AbacValidatorShared.MaxOrderIndex}.");

        RuleFor(x => x.Effect)
            .Must(AbacValidatorShared.EffectIsValid)
            .WithMessage("Effect must be one of the AbacEffect enum names (Allow / Deny).");

        RuleFor(x => x.ConditionExpression)
            .NotEmpty().WithMessage("ConditionExpression is required.")
            .MaximumLength(AbacValidatorShared.MaxConditionExpressionLength)
            .WithMessage($"ConditionExpression cannot exceed {AbacValidatorShared.MaxConditionExpressionLength} characters.")
            .Must(expr => parser.Parse(expr).IsSuccess)
            .WithMessage("ConditionExpression failed to parse against the ABAC grammar.");

        RuleFor(x => x.Description)
            .MaximumLength(AbacValidatorShared.MaxRuleDescriptionLength)
            .When(x => x.Description is not null)
            .WithMessage($"Description cannot exceed {AbacValidatorShared.MaxRuleDescriptionLength} characters.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates a single
/// <see cref="AbacRuleReorderInputDto"/> entry inside a bulk-reorder payload.
/// </summary>
public sealed class AbacRuleReorderInputValidator : AbstractValidator<AbacRuleReorderInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacRuleReorderInputValidator()
    {
        RuleFor(x => x.RuleSqid)
            .NotEmpty().WithMessage("RuleSqid is required.");

        RuleFor(x => x.NewOrderIndex)
            .InclusiveBetween(AbacValidatorShared.MinOrderIndex, AbacValidatorShared.MaxOrderIndex)
            .WithMessage($"NewOrderIndex must be in {AbacValidatorShared.MinOrderIndex}..{AbacValidatorShared.MaxOrderIndex}.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacExpressionTestInputDto"/>. Enforces
/// the policy-name regex and a per-dictionary key-count cap so a malicious caller
/// cannot blow up memory with an enormous attribute payload.
/// </summary>
public sealed class AbacExpressionTestInputValidator : AbstractValidator<AbacExpressionTestInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacExpressionTestInputValidator()
    {
        RuleFor(x => x.PolicyName)
            .Must(AbacValidatorShared.PolicyNameIsValid)
            .WithMessage("PolicyName must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$.");

        RuleFor(x => x.Subject)
            .NotNull().WithMessage("Subject dictionary is required (use an empty object {} when no attributes apply).")
            .Must(d => d is null || d.Count <= AbacValidatorShared.MaxAttributeKeys)
            .WithMessage($"Subject dictionary cannot exceed {AbacValidatorShared.MaxAttributeKeys} keys.");

        RuleFor(x => x.Resource)
            .NotNull().WithMessage("Resource dictionary is required.")
            .Must(d => d is null || d.Count <= AbacValidatorShared.MaxAttributeKeys)
            .WithMessage($"Resource dictionary cannot exceed {AbacValidatorShared.MaxAttributeKeys} keys.");

        RuleFor(x => x.Environment)
            .NotNull().WithMessage("Environment dictionary is required.")
            .Must(d => d is null || d.Count <= AbacValidatorShared.MaxAttributeKeys)
            .WithMessage($"Environment dictionary cannot exceed {AbacValidatorShared.MaxAttributeKeys} keys.");

        RuleFor(x => x.Action)
            .NotNull().WithMessage("Action dictionary is required.")
            .Must(d => d is null || d.Count <= AbacValidatorShared.MaxAttributeKeys)
            .WithMessage($"Action dictionary cannot exceed {AbacValidatorShared.MaxAttributeKeys} keys.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacRuleSetFilterDto"/>.
/// </summary>
public sealed class AbacRuleSetFilterValidator : AbstractValidator<AbacRuleSetFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacRuleSetFilterValidator()
    {
        RuleFor(x => x.PolicyName)
            .Must(AbacValidatorShared.PolicyNameIsValidOrNull)
            .WithMessage("PolicyName must be SCREAMING_SNAKE_CASE matching ^[A-Z][A-Z0-9_.]{1,63}$ when supplied.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(AbacValidatorShared.MaxTake)
            .WithMessage($"Take must be in 1..{AbacValidatorShared.MaxTake}.");
    }
}

/// <summary>
/// R2271 / TOR SEC 025 — validates <see cref="AbacRuleReasonInputDto"/>.
/// </summary>
public sealed class AbacRuleReasonInputValidator : AbstractValidator<AbacRuleReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public AbacRuleReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(AbacValidatorShared.MinReasonLength)
            .WithMessage($"Reason must be at least {AbacValidatorShared.MinReasonLength} characters.")
            .MaximumLength(AbacValidatorShared.MaxReasonLength)
            .WithMessage($"Reason cannot exceed {AbacValidatorShared.MaxReasonLength} characters.");
    }
}
