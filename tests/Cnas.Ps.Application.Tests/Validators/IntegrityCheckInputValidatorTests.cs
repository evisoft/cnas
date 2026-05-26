using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2282 / TOR SEC 036 — FluentValidation rules for the integrity-check
/// admin DTOs. Exercises the severity-name parse, the check-code regex,
/// the Skip/Take bounds, and the acknowledgement-note length window.
/// </summary>
public class IntegrityCheckInputValidatorTests
{
    private static IntegrityFindingFilterDto ValidFilter() => new(
        Severity: "Critical",
        AggregateName: "Claim",
        CheckCode: "CLAIM.RUNNING_TOTAL_MISMATCH",
        OnlyOpen: true,
        Skip: 0,
        Take: 50);

    [Fact]
    public void Filter_HappyPath_Passes()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(ValidFilter());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Filter_NullOptionalFields_Pass()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(new IntegrityFindingFilterDto(
            Severity: null,
            AggregateName: null,
            CheckCode: null,
            OnlyOpen: true,
            Skip: 0,
            Take: 25));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Filter_BadSeverity_Rejected()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(ValidFilter() with { Severity = "blocker" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrityFindingFilterDto.Severity));
    }

    [Fact]
    public void Filter_BadCheckCodeShape_Rejected()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(ValidFilter() with { CheckCode = "claim.lowercase" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrityFindingFilterDto.CheckCode));
    }

    [Fact]
    public void Filter_NegativeSkip_Rejected()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(ValidFilter() with { Skip = -1 });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Filter_TakeAboveMax_Rejected()
    {
        var v = new IntegrityFindingFilterValidator();
        var result = v.Validate(ValidFilter() with { Take = 500 });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrityFindingFilterDto.Take));
    }

    [Fact]
    public void Acknowledge_HappyPath_Passes()
    {
        var v = new IntegrityFindingAcknowledgeInputValidator();
        var result = v.Validate(new IntegrityFindingAcknowledgeInputDto("Investigated — operator confirmed the divergence is a data import artefact."));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Acknowledge_NoteTooShort_Rejected()
    {
        var v = new IntegrityFindingAcknowledgeInputValidator();
        var result = v.Validate(new IntegrityFindingAcknowledgeInputDto("ok"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Acknowledge_NoteTooLong_Rejected()
    {
        var v = new IntegrityFindingAcknowledgeInputValidator();
        var note = new string('x', 1001);
        var result = v.Validate(new IntegrityFindingAcknowledgeInputDto(note));
        result.IsValid.Should().BeFalse();
    }
}
