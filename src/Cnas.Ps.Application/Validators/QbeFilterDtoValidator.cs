using System.Text.RegularExpressions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0163 / TOR UI 009 — FluentValidation rules for <see cref="QbeFilterDto"/>. Runs at the
/// MVC binding boundary: rejects malformed combinators, oversized condition lists, and
/// individual conditions whose field names or values violate the shape limits. Semantic
/// checks (field is in schema, operator compatible with type) happen inside the converter,
/// which can surface a richer per-field error code than a validator's generic message.
/// </summary>
public sealed class QbeFilterDtoValidator : AbstractValidator<QbeFilterDto>
{
    /// <summary>
    /// Hard cap on the number of conditions per envelope. Protects against
    /// pathological payloads — the UI form (UI 009) renders a maximum of ~10 condition
    /// rows in practice; 25 leaves headroom for a power user without exposing an
    /// enumeration vector.
    /// </summary>
    internal const int MaxConditions = 25;

    /// <summary>
    /// Hard cap on the length of any single <see cref="QbeConditionDto.Value"/> /
    /// <see cref="QbeConditionDto.Value2"/>. Mirrors typical name + identifier lengths
    /// in the registry plus the comma-separated <c>In</c> list slack.
    /// </summary>
    internal const int MaxValueLength = 1024;

    /// <summary>
    /// Stable shape of a queryable field name. Anchored alphanumeric-with-underscores
    /// starting with a letter; max 64 chars so it can flow into the LINQ expression
    /// without any further character-class checks.
    /// </summary>
    internal const string FieldNamePattern = "^[a-zA-Z][a-zA-Z0-9_]{0,63}$";

    /// <summary>Creates the validator.</summary>
    public QbeFilterDtoValidator()
    {
        // Combinator must be the literal "AND" or "OR" — case-sensitive on purpose so a
        // lowercase spelling on the wire fails fast at the validator rather than silently
        // becoming "AND" via a normalisation step that would surprise future maintainers.
        RuleFor(x => x.Combinator)
            .NotEmpty().WithMessage("Combinator is required.")
            .Must(c => string.Equals(c, QbeFilter.CombinatorAnd, StringComparison.Ordinal)
                    || string.Equals(c, QbeFilter.CombinatorOr, StringComparison.Ordinal))
            .WithMessage($"Combinator must be one of: {QbeFilter.CombinatorAnd}, {QbeFilter.CombinatorOr}.");

        RuleFor(x => x.Conditions)
            .NotNull().WithMessage("Conditions list is required (use [] for none).")
            .Must(list => list is null || list.Count <= MaxConditions)
            .WithMessage($"Conditions list may not exceed {MaxConditions} entries.");

        RuleForEach(x => x.Conditions).SetValidator(new QbeConditionDtoValidator());
    }
}

/// <summary>
/// R0163 — per-row validator for <see cref="QbeConditionDto"/>. Runs as part of
/// <see cref="QbeFilterDtoValidator"/> via <c>RuleForEach.SetValidator</c>.
/// </summary>
public sealed class QbeConditionDtoValidator : AbstractValidator<QbeConditionDto>
{
    /// <summary>Creates the validator.</summary>
    public QbeConditionDtoValidator()
    {
        RuleFor(x => x.FieldName)
            .NotEmpty().WithMessage("FieldName is required.")
            .Matches(QbeFilterDtoValidator.FieldNamePattern)
            .WithMessage("FieldName must match ^[a-zA-Z][a-zA-Z0-9_]{0,63}$.");

        RuleFor(x => x.Operator)
            .NotEmpty().WithMessage("Operator is required.");

        RuleFor(x => x.Value)
            .Must(v => v is null || v.Length <= QbeFilterDtoValidator.MaxValueLength)
            .WithMessage($"Value may not exceed {QbeFilterDtoValidator.MaxValueLength} characters.");

        RuleFor(x => x.Value2)
            .Must(v => v is null || v.Length <= QbeFilterDtoValidator.MaxValueLength)
            .WithMessage($"Value2 may not exceed {QbeFilterDtoValidator.MaxValueLength} characters.");
    }
}

/// <summary>
/// R0163 — shared compiled regex helpers consumed by the QBE validator and the converter.
/// </summary>
public static class QbePatterns
{
    /// <summary>Compiled field-name regex with a 50 ms backtracking budget.</summary>
    public static readonly Regex FieldName = new(
        QbeFilterDtoValidator.FieldNamePattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
}
