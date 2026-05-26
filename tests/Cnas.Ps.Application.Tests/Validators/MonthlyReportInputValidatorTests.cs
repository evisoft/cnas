using System;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2461 / R2462 — unit tests for the monthly-report input validators
/// (<see cref="MonthlySupportReportInputValidator"/> +
/// <see cref="MonthlyErrorFixReportInputValidator"/>). Verifies the
/// first-of-month invariant and the not-in-the-future guard.
/// </summary>
public sealed class MonthlyReportInputValidatorTests
{
    /// <summary>Fixed clock reference — every test resolves "today" from here.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant for deterministic validation.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>R2461 — valid first-of-month input passes.</summary>
    [Fact]
    public void MonthlySupportReportInputValidator_AcceptsValidInput()
    {
        var validator = new MonthlySupportReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlySupportReportInputDto(
            Month: new DateOnly(2026, 4, 1),
            CategoryCodes: null);

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2461 — Day != 1 is rejected.</summary>
    [Fact]
    public void MonthlySupportReportInputValidator_RejectsMidMonthDay()
    {
        var validator = new MonthlySupportReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlySupportReportInputDto(
            Month: new DateOnly(2026, 4, 15),
            CategoryCodes: null);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("first day"));
    }

    /// <summary>R2461 — future month is rejected.</summary>
    [Fact]
    public void MonthlySupportReportInputValidator_RejectsFutureMonth()
    {
        var validator = new MonthlySupportReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlySupportReportInputDto(
            Month: new DateOnly(2027, 1, 1),
            CategoryCodes: null);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("future"));
    }

    /// <summary>R2461 — too many category codes rejected.</summary>
    [Fact]
    public void MonthlySupportReportInputValidator_RejectsTooManyCategoryCodes()
    {
        var validator = new MonthlySupportReportInputValidator(new StubClock(ClockNow));
        var codes = new string[MonthlySupportReportInputValidator.MaxCategoryCodes + 1];
        for (var i = 0; i < codes.Length; i++) codes[i] = $"CAT_{i}";
        var input = new MonthlySupportReportInputDto(
            Month: new DateOnly(2026, 4, 1),
            CategoryCodes: codes);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("exceed"));
    }

    /// <summary>R2461 — current month passes (boundary check).</summary>
    [Fact]
    public void MonthlySupportReportInputValidator_AcceptsCurrentMonth()
    {
        var validator = new MonthlySupportReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlySupportReportInputDto(
            Month: new DateOnly(2026, 5, 1),
            CategoryCodes: null);

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2462 — valid first-of-month input passes.</summary>
    [Fact]
    public void MonthlyErrorFixReportInputValidator_AcceptsValidInput()
    {
        var validator = new MonthlyErrorFixReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlyErrorFixReportInputDto(Month: new DateOnly(2026, 4, 1));

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2462 — Day != 1 is rejected.</summary>
    [Fact]
    public void MonthlyErrorFixReportInputValidator_RejectsMidMonthDay()
    {
        var validator = new MonthlyErrorFixReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlyErrorFixReportInputDto(Month: new DateOnly(2026, 4, 20));

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("first day"));
    }

    /// <summary>R2462 — future month rejected.</summary>
    [Fact]
    public void MonthlyErrorFixReportInputValidator_RejectsFutureMonth()
    {
        var validator = new MonthlyErrorFixReportInputValidator(new StubClock(ClockNow));
        var input = new MonthlyErrorFixReportInputDto(Month: new DateOnly(2030, 1, 1));

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("future"));
    }
}
