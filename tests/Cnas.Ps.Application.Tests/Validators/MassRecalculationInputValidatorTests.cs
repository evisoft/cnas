using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1503 / TOR §3.7-D — validates the FluentValidation rule sets shipped in
/// <see cref="MassRecalculationInputValidators"/> (see the
/// <c>MassRecalculationInputValidators.cs</c> file). Each test pins ONE
/// invariant so a regression is obvious from the failing test name.
/// </summary>
public sealed class MassRecalculationInputValidatorTests
{
    /// <summary>Default in-scope benefit-type list used by the happy-path register input.</summary>
    private static readonly string[] DefaultBenefitTypes = { "OldAgePension", "DisabilityPension" };

    /// <summary>Variant in-scope list with an unknown enum-name to exercise the validator's reject path.</summary>
    private static readonly string[] BenefitTypesWithUnknownEntry = { "OldAgePension", "TimeTravel" };

    private static LegalChangeEventRegisterInputDto ValidRegister(
        string? code = null,
        string scope = "Pension")
        => new(
            Code: code,
            Title: "Pension floor raise 2026-07",
            Description: "From 3000 to 3200 MDL",
            EffectiveFrom: new DateOnly(2026, 7, 1),
            Scope: scope,
            BenefitTypesInScope: DefaultBenefitTypes,
            ChangePayloadJson: "{\"minimumPensionMdl\":3200.00}");

    [Fact]
    public void Register_Valid_Passes()
    {
        var v = new LegalChangeEventRegisterInputValidator();
        var r = v.Validate(ValidRegister());
        r.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("lower-case")]            // not SCREAMING
    [InlineData("12_STARTS_WITH_DIGIT")]  // must start with letter
    [InlineData("HAS SPACE")]             // no space
    public void Register_BadCodePattern_Rejected(string badCode)
    {
        var v = new LegalChangeEventRegisterInputValidator();
        var r = v.Validate(ValidRegister(code: badCode));
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Register_InvalidJson_Rejected()
    {
        var v = new LegalChangeEventRegisterInputValidator();
        var bad = ValidRegister() with { ChangePayloadJson = "{not-json" };
        var r = v.Validate(bad);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Register_UnknownBenefitTypeEntry_Rejected()
    {
        var v = new LegalChangeEventRegisterInputValidator();
        var bad = ValidRegister() with { BenefitTypesInScope = BenefitTypesWithUnknownEntry };
        var r = v.Validate(bad);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Register_UnknownScope_Rejected()
    {
        var v = new LegalChangeEventRegisterInputValidator();
        var bad = ValidRegister(scope: "PensionXyz");
        var r = v.Validate(bad);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reject_TooShortReason_Rejected()
    {
        var v = new RecalculationResultRejectInputValidator();
        var r = v.Validate(new RecalculationResultRejectInputDto("ab"));
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reject_ValidReason_Passes()
    {
        var v = new RecalculationResultRejectInputValidator();
        var r = v.Validate(new RecalculationResultRejectInputDto("Operator excludes this row."));
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void RunFilter_TakeOutOfRange_Rejected()
    {
        var v = new RecalculationRunFilterValidator();
        var r = v.Validate(new RecalculationRunFilterDto(Mode: null, Status: null, LegalChangeSqid: null, Skip: 0, Take: 500));
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ResultFilter_TakeWithin200_Passes()
    {
        var v = new RecalculationResultFilterValidator();
        var r = v.Validate(new RecalculationResultFilterDto(Status: null, Skip: 0, Take: 200));
        r.IsValid.Should().BeTrue();
    }
}
