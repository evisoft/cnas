using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0573 / TOR CF 08.05 — FluentValidation rules for
/// <see cref="EmitNewDecisionInputDto"/>. Tests cover the happy path plus every
/// negative branch documented on <see cref="EmitNewDecisionInputValidator"/>:
/// empty template code, too-short template code, oversize notes, and
/// non-positive override amount.
/// </summary>
public sealed class EmitNewDecisionInputValidatorTests
{
    [Fact]
    public void Baseline_Succeeds()
    {
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "decizia-pensie",
            Notes: "Decizia emisă conform CF 08.05.",
            OverrideAmount: 1234.56m);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeTrue(string.Join("; ", r.Errors));
    }

    [Fact]
    public void RejectsEmptyTemplateCode()
    {
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: string.Empty,
            Notes: null,
            OverrideAmount: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(EmitNewDecisionInputDto.DecisionTemplateCode));
    }

    [Fact]
    public void RejectsTooShortTemplateCode()
    {
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "ab", // 2 chars — below the 3-char minimum
            Notes: null,
            OverrideAmount: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e =>
            e.PropertyName == nameof(EmitNewDecisionInputDto.DecisionTemplateCode)
            && e.ErrorMessage.Contains("at least", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsOversizeNotes()
    {
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "decizia-pensie",
            Notes: new string('x', EmitNewDecisionInputValidator.MaxNotesLength + 1),
            OverrideAmount: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(EmitNewDecisionInputDto.Notes));
    }

    [Fact]
    public void RejectsNonPositiveOverrideAmount()
    {
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "decizia-pensie",
            Notes: null,
            OverrideAmount: -1.00m);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(EmitNewDecisionInputDto.OverrideAmount));
    }

    [Fact]
    public void AcceptsNullOptionalFields()
    {
        // Notes + OverrideAmount default to null; the template-code alone should pass.
        var sut = new EmitNewDecisionInputValidator();
        var dto = new EmitNewDecisionInputDto(
            DecisionTemplateCode: "refuz-aplicare",
            Notes: null,
            OverrideAmount: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeTrue(string.Join("; ", r.Errors));
    }
}
