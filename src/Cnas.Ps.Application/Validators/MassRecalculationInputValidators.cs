using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1503 / TOR §3.7-D — shared helpers + constants for the mass-recalculation
/// validators. Centralised so the regex and length caps don't drift between
/// register / modify / filter rule sets.
/// </summary>
internal static partial class MassRecalculationValidatorShared
{
    /// <summary>Maximum permitted <c>Code</c> length (matches the entity column cap).</summary>
    public const int CodeMaxLength = 64;

    /// <summary>Minimum permitted <c>Title</c> length.</summary>
    public const int TitleMinLength = 3;

    /// <summary>Maximum permitted <c>Title</c> length.</summary>
    public const int TitleMaxLength = 256;

    /// <summary>Maximum permitted <c>Description</c> length.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>Maximum permitted <c>ChangePayloadJson</c> length.</summary>
    public const int PayloadJsonMaxLength = 16384;

    /// <summary>Maximum permitted explicit benefit-types-in-scope list size.</summary>
    public const int BenefitTypesInScopeMaxCount = 50;

    /// <summary>Minimum permitted reason length on reason / reject envelopes.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>Maximum permitted reason length on reason / reject envelopes.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum permitted run-filter <c>Take</c>.</summary>
    public const int MaxRunFilterTake = 100;

    /// <summary>Maximum permitted result-filter <c>Take</c>.</summary>
    public const int MaxResultFilterTake = 200;

    /// <summary>
    /// Code shape — uppercase ASCII letters / digits / underscore / dot,
    /// starting with a letter, 2..64 chars.
    /// </summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_.]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex CodeRegex();

    /// <summary>True when the supplied code is null OR matches the canonical shape.</summary>
    /// <param name="code">Candidate code.</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool CodeIsValidOrNull(string? code)
        => string.IsNullOrWhiteSpace(code) || CodeRegex().IsMatch(code);

    /// <summary>True when the supplied scope string parses to a valid <see cref="LegalChangeScope"/> name.</summary>
    /// <param name="scope">Candidate scope string.</param>
    /// <returns><c>true</c> when null or parse-conformant.</returns>
    public static bool ScopeIsValidOrNull(string? scope)
        => scope is null || Enum.TryParse<LegalChangeScope>(scope, ignoreCase: false, out _);

    /// <summary>True when every entry in the supplied list parses to a valid <see cref="BenefitType"/> enum-name.</summary>
    /// <param name="benefitTypes">Candidate list (may be null).</param>
    /// <returns><c>true</c> when null or every entry parses.</returns>
    public static bool BenefitTypesAreValidOrNull(System.Collections.Generic.IReadOnlyList<string>? benefitTypes)
    {
        if (benefitTypes is null)
        {
            return true;
        }
        foreach (var entry in benefitTypes)
        {
            if (!Enum.TryParse<BenefitType>(entry, ignoreCase: false, out _))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when the supplied JSON is null OR parses as a JSON document.</summary>
    /// <param name="json">Candidate JSON string.</param>
    /// <returns><c>true</c> when null or valid JSON.</returns>
    public static bool JsonIsValidOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="LegalChangeEventRegisterInputDto"/>.
/// </summary>
public sealed class LegalChangeEventRegisterInputValidator
    : AbstractValidator<LegalChangeEventRegisterInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LegalChangeEventRegisterInputValidator()
    {
        RuleFor(x => x.Code)
            .Must(MassRecalculationValidatorShared.CodeIsValidOrNull)
            .WithMessage("Code must match ^[A-Z][A-Z0-9_.]{1,63}$ when supplied.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(MassRecalculationValidatorShared.TitleMinLength)
            .WithMessage($"Title must be at least {MassRecalculationValidatorShared.TitleMinLength} characters.")
            .MaximumLength(MassRecalculationValidatorShared.TitleMaxLength)
            .WithMessage($"Title cannot exceed {MassRecalculationValidatorShared.TitleMaxLength} characters.");

        RuleFor(x => x.Description!)
            .MaximumLength(MassRecalculationValidatorShared.DescriptionMaxLength)
            .When(x => x.Description is not null)
            .WithMessage($"Description cannot exceed {MassRecalculationValidatorShared.DescriptionMaxLength} characters.");

        RuleFor(x => x.Scope)
            .NotEmpty().WithMessage("Scope is required.")
            .Must(s => MassRecalculationValidatorShared.ScopeIsValidOrNull(s))
            .WithMessage("Scope must be one of: Pension, UnemploymentBenefit, MaternityIndemnity, IncapacityIndemnity, SocialAid, All.");

        RuleFor(x => x.BenefitTypesInScope)
            .NotNull().WithMessage("BenefitTypesInScope is required (use an empty array when Scope=All).")
            .Must(b => b.Count <= MassRecalculationValidatorShared.BenefitTypesInScopeMaxCount)
            .WithMessage($"BenefitTypesInScope cannot exceed {MassRecalculationValidatorShared.BenefitTypesInScopeMaxCount} entries.")
            .Must(MassRecalculationValidatorShared.BenefitTypesAreValidOrNull)
            .WithMessage("Every BenefitTypesInScope entry must be a known BenefitType enum-name.");

        RuleFor(x => x.ChangePayloadJson)
            .MaximumLength(MassRecalculationValidatorShared.PayloadJsonMaxLength)
            .When(x => x.ChangePayloadJson is not null)
            .WithMessage($"ChangePayloadJson cannot exceed {MassRecalculationValidatorShared.PayloadJsonMaxLength} characters.")
            .Must(MassRecalculationValidatorShared.JsonIsValidOrNull)
            .WithMessage("ChangePayloadJson must be a valid JSON document when supplied.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="LegalChangeEventModifyInputDto"/>.
/// All fields nullable except <c>ChangeReason</c>.
/// </summary>
public sealed class LegalChangeEventModifyInputValidator
    : AbstractValidator<LegalChangeEventModifyInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LegalChangeEventModifyInputValidator()
    {
        RuleFor(x => x.Title!)
            .MinimumLength(MassRecalculationValidatorShared.TitleMinLength)
            .MaximumLength(MassRecalculationValidatorShared.TitleMaxLength)
            .When(x => x.Title is not null)
            .WithMessage($"Title must be {MassRecalculationValidatorShared.TitleMinLength}..{MassRecalculationValidatorShared.TitleMaxLength} chars when supplied.");

        RuleFor(x => x.Description!)
            .MaximumLength(MassRecalculationValidatorShared.DescriptionMaxLength)
            .When(x => x.Description is not null)
            .WithMessage($"Description cannot exceed {MassRecalculationValidatorShared.DescriptionMaxLength} characters.");

        RuleFor(x => x.Scope)
            .Must(s => MassRecalculationValidatorShared.ScopeIsValidOrNull(s))
            .WithMessage("Scope must be a known LegalChangeScope enum-name when supplied.");

        RuleFor(x => x.BenefitTypesInScope)
            .Must(b => b is null || b.Count <= MassRecalculationValidatorShared.BenefitTypesInScopeMaxCount)
            .WithMessage($"BenefitTypesInScope cannot exceed {MassRecalculationValidatorShared.BenefitTypesInScopeMaxCount} entries.")
            .Must(MassRecalculationValidatorShared.BenefitTypesAreValidOrNull)
            .WithMessage("Every BenefitTypesInScope entry must be a known BenefitType enum-name.");

        RuleFor(x => x.ChangePayloadJson)
            .MaximumLength(MassRecalculationValidatorShared.PayloadJsonMaxLength)
            .When(x => x.ChangePayloadJson is not null)
            .Must(MassRecalculationValidatorShared.JsonIsValidOrNull)
            .When(x => x.ChangePayloadJson is not null)
            .WithMessage("ChangePayloadJson must be a valid JSON document when supplied.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(MassRecalculationValidatorShared.ReasonMinLength)
            .MaximumLength(MassRecalculationValidatorShared.ReasonMaxLength)
            .WithMessage($"ChangeReason must be {MassRecalculationValidatorShared.ReasonMinLength}..{MassRecalculationValidatorShared.ReasonMaxLength} chars.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="LegalChangeEventReasonInputDto"/>.
/// </summary>
public sealed class LegalChangeEventReasonInputValidator
    : AbstractValidator<LegalChangeEventReasonInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public LegalChangeEventReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(MassRecalculationValidatorShared.ReasonMinLength)
            .MaximumLength(MassRecalculationValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {MassRecalculationValidatorShared.ReasonMinLength}..{MassRecalculationValidatorShared.ReasonMaxLength} chars.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="RecalculationResultRejectInputDto"/>.
/// </summary>
public sealed class RecalculationResultRejectInputValidator
    : AbstractValidator<RecalculationResultRejectInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public RecalculationResultRejectInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(MassRecalculationValidatorShared.ReasonMinLength)
            .MaximumLength(MassRecalculationValidatorShared.ReasonMaxLength)
            .WithMessage($"Reason must be {MassRecalculationValidatorShared.ReasonMinLength}..{MassRecalculationValidatorShared.ReasonMaxLength} chars.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="RecalculationRunFilterDto"/>.
/// </summary>
public sealed class RecalculationRunFilterValidator
    : AbstractValidator<RecalculationRunFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public RecalculationRunFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(MassRecalculationValidatorShared.MaxRunFilterTake)
            .WithMessage($"Take must be in 1..{MassRecalculationValidatorShared.MaxRunFilterTake}.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="RecalculationResultFilterDto"/>.
/// </summary>
public sealed class RecalculationResultFilterValidator
    : AbstractValidator<RecalculationResultFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public RecalculationResultFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(MassRecalculationValidatorShared.MaxResultFilterTake)
            .WithMessage($"Take must be in 1..{MassRecalculationValidatorShared.MaxResultFilterTake}.");
    }
}

/// <summary>
/// R1503 / TOR §3.7-D — validates <see cref="LegalChangeEventFilterDto"/>.
/// </summary>
public sealed class LegalChangeEventFilterValidator
    : AbstractValidator<LegalChangeEventFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public LegalChangeEventFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(MassRecalculationValidatorShared.MaxRunFilterTake)
            .WithMessage($"Take must be in 1..{MassRecalculationValidatorShared.MaxRunFilterTake}.");

        RuleFor(x => x.Status)
            .Must(s => s is null || Enum.TryParse<LegalChangeEventStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a known LegalChangeEventStatus enum-name when supplied.");

        RuleFor(x => x.Scope)
            .Must(s => MassRecalculationValidatorShared.ScopeIsValidOrNull(s))
            .WithMessage("Scope must be a known LegalChangeScope enum-name when supplied.");
    }
}
