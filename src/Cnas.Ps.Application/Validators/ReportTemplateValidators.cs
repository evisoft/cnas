using System.Text.RegularExpressions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — shared validation rules for the
/// <see cref="ReportTemplateCreateDto"/> / <see cref="ReportTemplateUpdateDto"/>
/// payloads. The semantic checks ("field exists in registry schema",
/// "GroupByField appears in SelectedFields") run at the validator layer because
/// they are wire-shape invariants — the service layer additionally guards them
/// against schema drift.
/// </summary>
public static class ReportTemplateValidationConstants
{
    /// <summary>Hard cap on selected fields per template; mirrors the validator rule.</summary>
    public const int MaxSelectedFields = 25;

    /// <summary>Hard cap on ordering specifications per template.</summary>
    public const int MaxOrderingEntries = 5;

    /// <summary>Code character pattern — kebab-case-ish identifier with dots and dashes.</summary>
    public const string CodePattern = "^[a-z][a-z0-9.-]{2,127}$";

    /// <summary>Max length of <see cref="ReportTemplateCreateDto.Name"/>.</summary>
    public const int MaxNameLength = 128;

    /// <summary>Max length of <see cref="ReportTemplateCreateDto.Description"/>.</summary>
    public const int MaxDescriptionLength = 512;

    /// <summary>Compiled code regex with a 50 ms backtracking budget.</summary>
    public static readonly Regex CodeRegex = new(
        CodePattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
}

/// <summary>
/// R0156 — FluentValidation rules for <see cref="ReportTemplateCreateDto"/>. Runs at
/// the MVC binding boundary; semantic invariants that depend on the live registry
/// schema (the unknown-field check) run inside the service layer where the
/// <see cref="IQbeRegistrySchemaProvider"/> is available.
/// </summary>
public sealed class ReportTemplateCreateDtoValidator : AbstractValidator<ReportTemplateCreateDto>
{
    /// <summary>Creates the validator.</summary>
    public ReportTemplateCreateDtoValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Must(c => c is not null && ReportTemplateValidationConstants.CodeRegex.IsMatch(c))
            .WithMessage("Code must match " + ReportTemplateValidationConstants.CodePattern + ".");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(ReportTemplateValidationConstants.MaxNameLength)
            .WithMessage($"Name may not exceed {ReportTemplateValidationConstants.MaxNameLength} characters.");

        RuleFor(x => x.Description)
            .MaximumLength(ReportTemplateValidationConstants.MaxDescriptionLength)
            .WithMessage($"Description may not exceed {ReportTemplateValidationConstants.MaxDescriptionLength} characters.");

        RuleFor(x => x.Registry)
            .NotEmpty().WithMessage("Registry is required.");

        RuleFor(x => x.SelectedFields)
            .NotNull().WithMessage("SelectedFields is required.")
            .Must(list => list is not null && list.Count >= 1)
            .WithMessage("At least one selected field is required.")
            .Must(list => list is null || list.Count <= ReportTemplateValidationConstants.MaxSelectedFields)
            .WithMessage($"SelectedFields may not exceed {ReportTemplateValidationConstants.MaxSelectedFields} entries.");

        RuleFor(x => x.Filter)
            .NotNull().WithMessage("Filter envelope is required (use empty conditions for no narrowing).");

        RuleFor(x => x.Ordering)
            .NotNull().WithMessage("Ordering is required (use [] for none).")
            .Must(list => list is null || list.Count <= ReportTemplateValidationConstants.MaxOrderingEntries)
            .WithMessage($"Ordering may not exceed {ReportTemplateValidationConstants.MaxOrderingEntries} entries.");

        // Group-by must appear in the selected fields when supplied. Combined check
        // because the rule depends on TWO properties on the DTO — using a custom
        // Must clause here keeps the message tight.
        When(x => !string.IsNullOrEmpty(x.GroupByField), () =>
        {
            RuleFor(x => x)
                .Must(d => d.SelectedFields is not null
                    && d.SelectedFields.Contains(d.GroupByField!, StringComparer.Ordinal))
                .WithMessage("GroupByField must also appear in SelectedFields.")
                .WithName(nameof(ReportTemplateCreateDto.GroupByField));
        });
    }
}

/// <summary>
/// R0156 — FluentValidation rules for <see cref="ReportTemplateUpdateDto"/>. Mirrors
/// the create-validator shape minus the registry / code rules (both are immutable).
/// </summary>
public sealed class ReportTemplateUpdateDtoValidator : AbstractValidator<ReportTemplateUpdateDto>
{
    /// <summary>Creates the validator.</summary>
    public ReportTemplateUpdateDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(ReportTemplateValidationConstants.MaxNameLength)
            .WithMessage($"Name may not exceed {ReportTemplateValidationConstants.MaxNameLength} characters.");

        RuleFor(x => x.Description)
            .MaximumLength(ReportTemplateValidationConstants.MaxDescriptionLength)
            .WithMessage($"Description may not exceed {ReportTemplateValidationConstants.MaxDescriptionLength} characters.");

        RuleFor(x => x.SelectedFields)
            .NotNull().WithMessage("SelectedFields is required.")
            .Must(list => list is not null && list.Count >= 1)
            .WithMessage("At least one selected field is required.")
            .Must(list => list is null || list.Count <= ReportTemplateValidationConstants.MaxSelectedFields)
            .WithMessage($"SelectedFields may not exceed {ReportTemplateValidationConstants.MaxSelectedFields} entries.");

        RuleFor(x => x.Filter)
            .NotNull().WithMessage("Filter envelope is required (use empty conditions for no narrowing).");

        RuleFor(x => x.Ordering)
            .NotNull().WithMessage("Ordering is required (use [] for none).")
            .Must(list => list is null || list.Count <= ReportTemplateValidationConstants.MaxOrderingEntries)
            .WithMessage($"Ordering may not exceed {ReportTemplateValidationConstants.MaxOrderingEntries} entries.");

        When(x => !string.IsNullOrEmpty(x.GroupByField), () =>
        {
            RuleFor(x => x)
                .Must(d => d.SelectedFields is not null
                    && d.SelectedFields.Contains(d.GroupByField!, StringComparer.Ordinal))
                .WithMessage("GroupByField must also appear in SelectedFields.")
                .WithName(nameof(ReportTemplateUpdateDto.GroupByField));
        });
    }
}
