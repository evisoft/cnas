using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0203 / TOR CF 20.06 — validates <see cref="ExternalSourceManualTriggerInputDto"/>.
/// Pins the SourceCode regex + the as-of-date upper bound (cannot be in the
/// future relative to the operating UTC day).
/// </summary>
public sealed class ExternalSourceManualTriggerInputValidator
    : AbstractValidator<ExternalSourceManualTriggerInputDto>
{
    /// <summary>
    /// Stable source-code regex — upper-case alphanumeric with optional dotted
    /// namespace segments. Aligns with the audit-category regex so operators
    /// have one mental model across both surfaces.
    /// </summary>
    public const string SourceCodeRegex = "^[A-Z][A-Z0-9_.]{1,63}$";

    /// <summary>Compiled <see cref="SourceCodeRegex"/> instance.</summary>
    private static readonly Regex CompiledSourceCode = new(
        SourceCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Builds the validator. Clock is injected by the service at validate time.</summary>
    public ExternalSourceManualTriggerInputValidator()
    {
        RuleFor(x => x.SourceCode)
            .NotEmpty().WithMessage("SourceCode is required.")
            .MaximumLength(64).WithMessage("SourceCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledSourceCode.IsMatch(s))
            .WithMessage("SourceCode must match the SCREAMING_SNAKE_CASE pattern (dots allowed for namespaces).");
    }

    /// <summary>
    /// Returns the not-in-future violation message for the supplied as-of
    /// date, or <c>null</c> when the date is acceptable. Used by the service
    /// at the validation boundary because the date check requires the live
    /// UTC clock.
    /// </summary>
    /// <param name="asOfDate">As-of date supplied by the caller.</param>
    /// <param name="todayUtc">Current UTC day.</param>
    /// <returns>Violation message, or null when acceptable.</returns>
    public static string? ValidateAsOfDate(DateOnly? asOfDate, DateOnly todayUtc)
    {
        if (asOfDate is null)
        {
            return null;
        }
        if (asOfDate.Value > todayUtc)
        {
            return "AsOfDate cannot be in the future.";
        }
        if (asOfDate.Value < todayUtc.AddDays(-365))
        {
            return "AsOfDate cannot be older than 365 days.";
        }
        return null;
    }
}

/// <summary>
/// R0203 / TOR CF 20.06 — validates the runs-list filter envelope. Pins paging
/// caps + enum-name membership for status and trigger filters.
/// </summary>
public sealed class ExternalSourceIngestionRunFilterValidator
    : AbstractValidator<ExternalSourceIngestionRunFilterDto>
{
    /// <summary>Maximum allowed page size for the runs-list endpoint.</summary>
    public const int MaxTake = 100;

    /// <summary>Builds the validator.</summary>
    public ExternalSourceIngestionRunFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");

        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be greater than 0.")
            .LessThanOrEqualTo(MaxTake).WithMessage($"Take must be {MaxTake} or fewer.");

        RuleFor(x => x.Status)
            .Must(IsKnownStatusOrNull)
            .WithMessage("Status must be a stable ExternalSourceIngestionStatus enum-name.");

        RuleFor(x => x.TriggerKind)
            .Must(IsKnownTriggerOrNull)
            .WithMessage("TriggerKind must be a stable ExternalSourceTriggerKind enum-name.");

        RuleFor(x => x.SourceCode)
            .MaximumLength(64).WithMessage("SourceCode must be 64 characters or fewer.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="status"/> is null or a known enum-name.</summary>
    /// <param name="status">Status filter candidate.</param>
    /// <returns><c>true</c> iff acceptable.</returns>
    internal static bool IsKnownStatusOrNull(string? status)
        => string.IsNullOrWhiteSpace(status)
           || Enum.TryParse<ExternalSourceIngestionStatus>(status, ignoreCase: false, out _);

    /// <summary>Returns <c>true</c> when <paramref name="trigger"/> is null or a known enum-name.</summary>
    /// <param name="trigger">Trigger filter candidate.</param>
    /// <returns><c>true</c> iff acceptable.</returns>
    internal static bool IsKnownTriggerOrNull(string? trigger)
        => string.IsNullOrWhiteSpace(trigger)
           || Enum.TryParse<ExternalSourceTriggerKind>(trigger, ignoreCase: false, out _);
}
