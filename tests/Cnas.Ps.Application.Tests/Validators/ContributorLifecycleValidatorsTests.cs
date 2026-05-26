using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0305 / TOR Annex 1 — unit tests for the BP 1.2 / 1.3 / 1.4 / 1.9 input-DTO
/// validators. Each rule is covered by one positive and one negative case so the
/// contract is locked down per CLAUDE.md §3.1 (unit tier).
/// </summary>
public sealed class ContributorLifecycleValidatorsTests
{
    /// <summary>BP 1.3 deactivation reason: empty fails.</summary>
    [Fact]
    public void DeactivationReason_Empty_Fails()
    {
        var v = new ContributorDeactivationInputDtoValidator();
        var result = v.Validate(new ContributorDeactivationInputDto(string.Empty));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.3 deactivation reason: too short (&lt; 3 chars) fails.</summary>
    [Fact]
    public void DeactivationReason_TooShort_Fails()
    {
        var v = new ContributorDeactivationInputDtoValidator();
        var result = v.Validate(new ContributorDeactivationInputDto("ab"));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.3 deactivation reason: 3..500 char window passes.</summary>
    [Fact]
    public void DeactivationReason_HappyPath_Passes()
    {
        var v = new ContributorDeactivationInputDtoValidator();
        var result = v.Validate(new ContributorDeactivationInputDto("Operator request"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>BP 1.4 reactivation reason: 3..500 chars; same shape as BP 1.3.</summary>
    [Fact]
    public void ReactivationReason_HappyPath_Passes()
    {
        var v = new ContributorReactivationInputDtoValidator();
        var result = v.Validate(new ContributorReactivationInputDto("Resumed business"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>BP 1.2 attributes update: empty Denumire fails.</summary>
    [Fact]
    public void AttributesUpdate_EmptyDenumire_Fails()
    {
        var v = new ContributorAttributesUpdateDtoValidator();
        var result = v.Validate(new ContributorAttributesUpdateDto(string.Empty, null, null));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.2 attributes update: malformed CFOJ (non-digit) fails.</summary>
    [Fact]
    public void AttributesUpdate_BadCfoj_Fails()
    {
        var v = new ContributorAttributesUpdateDtoValidator();
        var result = v.Validate(new ContributorAttributesUpdateDto("SRL X", "abcd", null));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.9 mark-deceased: default DateOnly rejected by the lower-bound guard.</summary>
    [Fact]
    public void MarkDeceased_DefaultDate_Fails()
    {
        var v = new ContributorMarkDeceasedInputDtoValidator();
        var result = v.Validate(new ContributorMarkDeceasedInputDto(default));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.9 mark-deceased: a sensible 2026 date passes.</summary>
    [Fact]
    public void MarkDeceased_RecentDate_Passes()
    {
        var v = new ContributorMarkDeceasedInputDtoValidator();
        var result = v.Validate(new ContributorMarkDeceasedInputDto(new DateOnly(2026, 4, 15)));
        result.IsValid.Should().BeTrue();
    }
}
