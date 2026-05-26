using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0819 / R0820 — unit tests for the late-penalty + management-period-close
/// validators. Each contract clause is locked down with one positive and one
/// negative case so the wire shape stays stable across releases.
/// </summary>
public sealed class LatePenaltyAndPeriodCloseValidatorsTests
{
    /// <summary>Canonical first-of-month anchor used across the suite.</summary>
    private static readonly DateOnly FirstOfMonth = new(2026, 4, 1);

    /// <summary>R0819 — happy path: month day=1 and upToDate >= month passes.</summary>
    [Fact]
    public void PenaltyCalculate_HappyPath_Passes()
    {
        var v = new LatePaymentPenaltyCalculateInputDtoValidator();
        var result = v.Validate(new LatePaymentPenaltyCalculateInputDto(
            Month: FirstOfMonth,
            UpToDate: new DateOnly(2026, 5, 10)));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0819 — non-first-of-month is rejected.</summary>
    [Fact]
    public void PenaltyCalculate_BadDay_Fails()
    {
        var v = new LatePaymentPenaltyCalculateInputDtoValidator();
        var result = v.Validate(new LatePaymentPenaltyCalculateInputDto(
            Month: new DateOnly(2026, 4, 15),
            UpToDate: new DateOnly(2026, 5, 10)));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0819 — UpToDate earlier than the reporting month is rejected.</summary>
    [Fact]
    public void PenaltyCalculate_UpToDateBeforeMonth_Fails()
    {
        var v = new LatePaymentPenaltyCalculateInputDtoValidator();
        var result = v.Validate(new LatePaymentPenaltyCalculateInputDto(
            Month: FirstOfMonth,
            UpToDate: new DateOnly(2026, 3, 31)));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0819 — waive reason of 3..500 chars passes.</summary>
    [Fact]
    public void Waive_HappyPath_Passes()
    {
        var v = new LatePaymentPenaltyWaiveInputDtoValidator();
        var result = v.Validate(new LatePaymentPenaltyWaiveInputDto("Court-ordered remission"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0819 — empty waive reason is rejected.</summary>
    [Fact]
    public void Waive_EmptyReason_Fails()
    {
        var v = new LatePaymentPenaltyWaiveInputDtoValidator();
        var result = v.Validate(new LatePaymentPenaltyWaiveInputDto(""));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0820 — close input with day=1 month passes; optional notes ≤ 1000 chars.</summary>
    [Fact]
    public void Close_HappyPath_Passes()
    {
        var v = new ManagementPeriodCloseInputDtoValidator();
        var result = v.Validate(new ManagementPeriodCloseInputDto(FirstOfMonth, "All good"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0820 — non-first-of-month close is rejected.</summary>
    [Fact]
    public void Close_BadDay_Fails()
    {
        var v = new ManagementPeriodCloseInputDtoValidator();
        var result = v.Validate(new ManagementPeriodCloseInputDto(
            Month: new DateOnly(2026, 4, 30),
            Notes: null));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0820 — re-open input with valid month + 3..500-char reason passes.</summary>
    [Fact]
    public void Reopen_HappyPath_Passes()
    {
        var v = new ManagementPeriodReopenInputDtoValidator();
        var result = v.Validate(new ManagementPeriodReopenInputDto(
            Month: FirstOfMonth,
            Reason: "Adjustment found after close"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0820 — empty re-open reason is rejected.</summary>
    [Fact]
    public void Reopen_EmptyReason_Fails()
    {
        var v = new ManagementPeriodReopenInputDtoValidator();
        var result = v.Validate(new ManagementPeriodReopenInputDto(FirstOfMonth, ""));
        result.IsValid.Should().BeFalse();
    }
}
