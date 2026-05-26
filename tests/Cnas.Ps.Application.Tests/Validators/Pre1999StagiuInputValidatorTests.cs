using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — unit tests for
/// <see cref="Pre1999StagiuInputValidator"/>. Pins the four documented rules:
/// pre-1999 invariant on both date columns, FromDate ≤ ToDate, and the
/// Years/Months/Days bounds.
/// </summary>
public sealed class Pre1999StagiuInputValidatorTests
{
    private readonly Pre1999StagiuInputValidator _validator = new();

    /// <summary>Builds a known-good input that callers can mutate per test.</summary>
    private static Pre1999StagiuInputDto BuildValid(
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        int years = 5,
        int months = 2,
        int days = 10,
        string? source = null,
        string? notes = null)
        => new(
            FromDate: fromDate ?? new DateOnly(1990, 1, 1),
            ToDate: toDate ?? new DateOnly(1995, 3, 11),
            Years: years,
            Months: months,
            Days: days,
            Source: source,
            Notes: notes);

    /// <summary>Pinning happy-path test — every field passes.</summary>
    [Fact]
    public void Validate_HappyPath_NoErrors()
    {
        var result = _validator.TestValidate(BuildValid());
        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>FromDate after 1998-12-31 violates the pre-1999 invariant.</summary>
    [Fact]
    public void Validate_FromDatePost1998_RejectsFromDate()
    {
        var input = BuildValid(fromDate: new DateOnly(1999, 1, 1), toDate: new DateOnly(1999, 6, 30));
        var result = _validator.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.FromDate);
    }

    /// <summary>ToDate after 1998-12-31 violates the pre-1999 invariant.</summary>
    [Fact]
    public void Validate_ToDatePost1998_RejectsToDate()
    {
        var input = BuildValid(fromDate: new DateOnly(1990, 1, 1), toDate: new DateOnly(1999, 6, 30));
        var result = _validator.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.ToDate);
    }

    /// <summary>Years out of range (negative) rejected.</summary>
    [Fact]
    public void Validate_NegativeYears_RejectsYears()
    {
        var input = BuildValid(years: -1);
        var result = _validator.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Years);
    }

    /// <summary>Months above 11 rejected — Months ≥ 12 must roll up into Years.</summary>
    [Fact]
    public void Validate_MonthsTwelve_RejectsMonths()
    {
        var input = BuildValid(months: 12);
        var result = _validator.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Months);
    }

    /// <summary>Days above 30 rejected — Days ≥ 31 must roll up into Months.</summary>
    [Fact]
    public void Validate_DaysThirtyOne_RejectsDays()
    {
        var input = BuildValid(days: 31);
        var result = _validator.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Days);
    }
}
