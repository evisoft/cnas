using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2279 / TOR SEC 033 — shared constants for the classification-catalog
/// validators. Centralised so the magic numbers do not drift across rule sets.
/// </summary>
internal static class ClassificationCatalogValidatorShared
{
    /// <summary>Maximum permitted <c>Take</c> on the snapshot-details endpoint.</summary>
    public const int EntryTakeMaxPageSize = 500;

    /// <summary>Maximum permitted <c>Take</c> on the drift-findings endpoint.</summary>
    public const int DriftTakeMaxPageSize = 200;

    /// <summary>Maximum permitted length of the <c>TypeFullNameContains</c> substring.</summary>
    public const int TypeFullNameContainsMaxLength = 512;

    /// <summary>Minimum acknowledgement-note length.</summary>
    public const int NoteMinLength = 3;

    /// <summary>Maximum acknowledgement-note length.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>True when the supplied label string parses to a valid <see cref="SensitivityLabel"/> name, or is null.</summary>
    /// <param name="label">Candidate label name (case-sensitive).</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool LabelIsValidOrNull(string? label)
        => label is null
            || System.Enum.TryParse<SensitivityLabel>(label, ignoreCase: false, out _);

    /// <summary>True when the supplied drift-kind string parses to a valid
    /// <see cref="Cnas.Ps.Core.Domain.ClassificationDriftKind"/> name, or is null.</summary>
    /// <param name="driftKind">Candidate drift-kind name (case-sensitive).</param>
    /// <returns><c>true</c> when null or shape-conformant.</returns>
    public static bool DriftKindIsValidOrNull(string? driftKind)
        => driftKind is null
            || System.Enum.TryParse<Cnas.Ps.Core.Domain.ClassificationDriftKind>(driftKind, ignoreCase: false, out _);
}

/// <summary>
/// R2279 / TOR SEC 033 — validates <see cref="ClassificationCatalogEntryFilterDto"/>.
/// Enforces the label-name parse, the substring-length cap, and the page bounds.
/// </summary>
public sealed class ClassificationCatalogEntryFilterValidator
    : AbstractValidator<ClassificationCatalogEntryFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClassificationCatalogEntryFilterValidator()
    {
        RuleFor(x => x.Label)
            .Must(ClassificationCatalogValidatorShared.LabelIsValidOrNull)
            .WithMessage("Label must be one of: Public, Internal, Confidential, Restricted — or null to match any.");

        RuleFor(x => x.TypeFullNameContains!)
            .MaximumLength(ClassificationCatalogValidatorShared.TypeFullNameContainsMaxLength)
            .When(x => x.TypeFullNameContains is not null)
            .WithMessage(
                $"TypeFullNameContains cannot exceed {ClassificationCatalogValidatorShared.TypeFullNameContainsMaxLength} characters.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(ClassificationCatalogValidatorShared.EntryTakeMaxPageSize)
            .WithMessage($"Take must be in 1..{ClassificationCatalogValidatorShared.EntryTakeMaxPageSize}.");
    }
}

/// <summary>
/// R2279 / TOR SEC 033 — validates <see cref="ClassificationDriftFilterDto"/>.
/// Enforces the drift-kind name parse and the page bounds.
/// </summary>
public sealed class ClassificationDriftFilterValidator
    : AbstractValidator<ClassificationDriftFilterDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClassificationDriftFilterValidator()
    {
        RuleFor(x => x.DriftKind)
            .Must(ClassificationCatalogValidatorShared.DriftKindIsValidOrNull)
            .WithMessage("DriftKind must be one of: Added, Removed, LabelChanged, ClassificationLost — or null to match any.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .GreaterThanOrEqualTo(1)
            .LessThanOrEqualTo(ClassificationCatalogValidatorShared.DriftTakeMaxPageSize)
            .WithMessage($"Take must be in 1..{ClassificationCatalogValidatorShared.DriftTakeMaxPageSize}.");
    }
}

/// <summary>
/// R2279 / TOR SEC 033 — validates <see cref="ClassificationDriftAcknowledgeInputDto"/>.
/// The note must be present and 3..1000 chars.
/// </summary>
public sealed class ClassificationDriftAcknowledgeInputValidator
    : AbstractValidator<ClassificationDriftAcknowledgeInputDto>
{
    /// <summary>Builds the rule set.</summary>
    public ClassificationDriftAcknowledgeInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(ClassificationCatalogValidatorShared.NoteMinLength)
            .WithMessage(
                $"Note must be at least {ClassificationCatalogValidatorShared.NoteMinLength} characters.")
            .MaximumLength(ClassificationCatalogValidatorShared.NoteMaxLength)
            .WithMessage(
                $"Note cannot exceed {ClassificationCatalogValidatorShared.NoteMaxLength} characters.");
    }
}
