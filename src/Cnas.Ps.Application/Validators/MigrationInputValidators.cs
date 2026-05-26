using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2430 / TOR M4 — validates <see cref="MigrationPlanCreateInputDto"/>.
/// Pins the plan-code regex, title/description length bounds, source-kind
/// enum membership, target-entity-name length, batch-size range, and the
/// well-formed-JSON shape of the mapping descriptor (when supplied).
/// </summary>
public sealed class MigrationPlanCreateInputValidator : AbstractValidator<MigrationPlanCreateInputDto>
{
    /// <summary>Stable PlanCode regex — SCREAMING_SNAKE_CASE with optional dots, ≤ 64 chars.</summary>
    public const string PlanCodeRegex = "^[A-Z][A-Z0-9_.]{1,63}$";

    /// <summary>Compiled <see cref="PlanCodeRegex"/> instance.</summary>
    private static readonly Regex CompiledPlanCode = new(
        PlanCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Lower bound on BatchSize.</summary>
    public const int MinBatchSize = 10;

    /// <summary>Upper bound on BatchSize.</summary>
    public const int MaxBatchSize = 10000;

    /// <summary>Maximum length of the mapping-descriptor JSON payload.</summary>
    public const int MaxMappingDescriptorJsonLength = 16384;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationPlanCreateInputValidator()
    {
        RuleFor(x => x.PlanCode)
            .NotEmpty().WithMessage("PlanCode is required.")
            .MaximumLength(64).WithMessage("PlanCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledPlanCode.IsMatch(s))
            .WithMessage("PlanCode must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.SourceKind)
            .NotEmpty().WithMessage("SourceKind is required.")
            .Must(IsKnownSourceKind)
            .WithMessage("SourceKind must be a stable MigrationSourceKind enum-name.");

        RuleFor(x => x.TargetEntityName)
            .NotEmpty().WithMessage("TargetEntityName is required.")
            .MaximumLength(128).WithMessage("TargetEntityName must be 128 characters or fewer.");

        RuleFor(x => x.BatchSize)
            .InclusiveBetween(MinBatchSize, MaxBatchSize)
            .WithMessage($"BatchSize must be in [{MinBatchSize}, {MaxBatchSize}].");

        RuleFor(x => x.MappingDescriptorJson)
            .MaximumLength(MaxMappingDescriptorJsonLength)
            .WithMessage($"MappingDescriptorJson must be {MaxMappingDescriptorJsonLength} characters or fewer.")
            .Must(BeWellFormedJsonOrNull)
            .WithMessage("MappingDescriptorJson must be well-formed JSON when supplied.");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="sourceKind"/> parses to a
    /// <see cref="MigrationSourceKind"/>.
    /// </summary>
    /// <param name="sourceKind">Candidate source-kind string.</param>
    /// <returns><c>true</c> iff the string is a known enum-name.</returns>
    private static bool IsKnownSourceKind(string? sourceKind)
        => !string.IsNullOrWhiteSpace(sourceKind)
           && Enum.TryParse<MigrationSourceKind>(sourceKind, ignoreCase: false, out _);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="json"/> is null/whitespace
    /// or a well-formed JSON document.
    /// </summary>
    /// <param name="json">Candidate JSON payload.</param>
    /// <returns><c>true</c> iff the input is null or parsable as JSON.</returns>
    private static bool BeWellFormedJsonOrNull(string? json)
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
/// R2430 / TOR M4 — validates <see cref="MigrationPlanModifyInputDto"/>.
/// </summary>
public sealed class MigrationPlanModifyInputValidator : AbstractValidator<MigrationPlanModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationPlanModifyInputValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.BatchSize)
            .InclusiveBetween(MigrationPlanCreateInputValidator.MinBatchSize, MigrationPlanCreateInputValidator.MaxBatchSize)
            .WithMessage($"BatchSize must be in [{MigrationPlanCreateInputValidator.MinBatchSize}, {MigrationPlanCreateInputValidator.MaxBatchSize}].");

        RuleFor(x => x.MappingDescriptorJson)
            .MaximumLength(MigrationPlanCreateInputValidator.MaxMappingDescriptorJsonLength)
            .WithMessage($"MappingDescriptorJson must be {MigrationPlanCreateInputValidator.MaxMappingDescriptorJsonLength} characters or fewer.");
    }
}

/// <summary>
/// R2430 / TOR M4 — validates <see cref="MigrationPlanReasonInputDto"/>.
/// </summary>
public sealed class MigrationPlanReasonInputValidator : AbstractValidator<MigrationPlanReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationPlanReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>
/// R2433 / TOR M4 — validates <see cref="MigrationFindingAcknowledgeInputDto"/>.
/// </summary>
public sealed class MigrationFindingAcknowledgeInputValidator
    : AbstractValidator<MigrationFindingAcknowledgeInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationFindingAcknowledgeInputValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("Note is required.")
            .MinimumLength(3).WithMessage("Note must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Note must be 1000 characters or fewer.");
    }
}

/// <summary>R2430 / R2433 / TOR M4 — validates <see cref="MigrationFindingFilterDto"/>.</summary>
public sealed class MigrationFindingFilterValidator : AbstractValidator<MigrationFindingFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationFindingFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Severity)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MigrationFindingSeverity>(s, ignoreCase: false, out _))
            .WithMessage("Severity must be a stable MigrationFindingSeverity enum-name when supplied.");

        RuleFor(x => x.FindingCode)
            .MaximumLength(64).WithMessage("FindingCode must be 64 characters or fewer.");
    }
}

/// <summary>R2430 / TOR M4 — validates <see cref="MigrationRunDetailsFilterDto"/>.</summary>
public sealed class MigrationRunDetailsFilterValidator : AbstractValidator<MigrationRunDetailsFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationRunDetailsFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");
    }
}

/// <summary>R2430 / TOR M4 — validates <see cref="MigrationRunFilterDto"/>.</summary>
public sealed class MigrationRunFilterValidator : AbstractValidator<MigrationRunFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationRunFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MigrationRunStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable MigrationRunStatus enum-name when supplied.");

        RuleFor(x => x.TriggerKind)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MigrationTriggerKind>(s, ignoreCase: false, out _))
            .WithMessage("TriggerKind must be a stable MigrationTriggerKind enum-name when supplied.");
    }
}

/// <summary>R2430 / TOR M4 — validates <see cref="MigrationPlanFilterDto"/>.</summary>
public sealed class MigrationPlanFilterValidator : AbstractValidator<MigrationPlanFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MigrationPlanFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MigrationPlanStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable MigrationPlanStatus enum-name when supplied.");

        RuleFor(x => x.TargetEntityName)
            .MaximumLength(128)
            .WithMessage("TargetEntityName must be 128 characters or fewer.");
    }
}
