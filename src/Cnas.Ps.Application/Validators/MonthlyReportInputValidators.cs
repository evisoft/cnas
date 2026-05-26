using System;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2461 / Deliverable 7.1 — validates <see cref="MonthlySupportReportInputDto"/>.
/// Enforces the first-of-month invariant on <c>Month</c> (Day must equal 1)
/// and rejects month values strictly in the future relative to the configured
/// <see cref="ICnasTimeProvider"/>. The optional <c>CategoryCodes</c> list is
/// bounded to a sensible page so a malicious caller cannot send a million
/// codes and force a quadratic IN-clause expansion.
/// </summary>
public sealed class MonthlySupportReportInputValidator
    : AbstractValidator<MonthlySupportReportInputDto>
{
    /// <summary>Maximum number of distinct category codes permitted on the filter.</summary>
    public const int MaxCategoryCodes = 100;

    /// <summary>Maximum length of an individual category code (matches the entity column cap).</summary>
    public const int MaxCategoryCodeLength = 64;

    /// <summary>
    /// Constructs the validator. The <paramref name="clock"/> is captured so
    /// the "not in the future" check uses the system's deterministic UTC
    /// instead of <see cref="DateTime.UtcNow"/> directly (CLAUDE.md RULE 4).
    /// </summary>
    /// <param name="clock">UTC time provider; used to derive "today's month".</param>
    /// <exception cref="ArgumentNullException">When <paramref name="clock"/> is null.</exception>
    public MonthlySupportReportInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(x => x.Month)
            .Must(m => m.Day == 1)
            .WithMessage("Month must be the first day of a calendar month.");

        RuleFor(x => x.Month)
            .Must(m =>
            {
                // Compare the requested month to the clock's current month (UTC).
                // A month strictly later than the clock's current month is rejected.
                var today = DateOnly.FromDateTime(clock.UtcNow);
                var currentMonth = new DateOnly(today.Year, today.Month, 1);
                return m <= currentMonth;
            })
            .WithMessage("Month cannot be in the future.");

        RuleFor(x => x.CategoryCodes!)
            .Must(list => list is null || list.Count <= MaxCategoryCodes)
            .WithMessage($"CategoryCodes cannot exceed {MaxCategoryCodes} entries.")
            .When(x => x.CategoryCodes is not null);

        RuleForEach(x => x.CategoryCodes!)
            .NotEmpty().WithMessage("CategoryCode entry cannot be empty.")
            .MaximumLength(MaxCategoryCodeLength)
            .WithMessage($"CategoryCode entry must be ≤ {MaxCategoryCodeLength} chars.")
            .When(x => x.CategoryCodes is not null);
    }
}

/// <summary>
/// R2462 / Deliverable 7.2 — validates <see cref="MonthlyErrorFixReportInputDto"/>.
/// Shares the same first-of-month and "not in the future" rules as
/// <see cref="MonthlySupportReportInputValidator"/>.
/// </summary>
public sealed class MonthlyErrorFixReportInputValidator
    : AbstractValidator<MonthlyErrorFixReportInputDto>
{
    /// <summary>
    /// Constructs the validator. The <paramref name="clock"/> is captured so
    /// the "not in the future" check uses the system's deterministic UTC
    /// (CLAUDE.md RULE 4).
    /// </summary>
    /// <param name="clock">UTC time provider; used to derive "today's month".</param>
    /// <exception cref="ArgumentNullException">When <paramref name="clock"/> is null.</exception>
    public MonthlyErrorFixReportInputValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(x => x.Month)
            .Must(m => m.Day == 1)
            .WithMessage("Month must be the first day of a calendar month.");

        RuleFor(x => x.Month)
            .Must(m =>
            {
                var today = DateOnly.FromDateTime(clock.UtcNow);
                var currentMonth = new DateOnly(today.Year, today.Month, 1);
                return m <= currentMonth;
            })
            .WithMessage("Month cannot be in the future.");
    }
}
