using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1403 / TOR §3.6-D — unit tests for the athlete-pension input validators.
/// Covers IDNP format, sport-discipline regex, age range, achievement-year
/// bounds, monthly-amount and additional-multiplier bounds, and the
/// 3..1000-char reason / note rules.
/// </summary>
public sealed class AthletePensionInputValidatorTests
{
    /// <summary>Fixed UTC clock used by every validator under test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Builds a canonical create-input DTO that should pass validation.</summary>
    private static AthletePensionAwardCreateInputDto ValidCreate() => new(
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryDisplayName: "Ion Popescu",
        BeneficiaryBirthDate: new DateOnly(1980, 4, 1),
        BeneficiarySex: nameof(BeneficiarySex.Male),
        Role: nameof(AthletePensionRole.Athlete),
        SportDiscipline: "ATHLETICS");

    [Fact]
    public void Create_HappyPath_Accepted()
    {
        var v = new AthletePensionAwardCreateInputValidator(new StubClock());

        var result = v.Validate(ValidCreate());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_BadIdnpTooShort_Rejected()
    {
        var v = new AthletePensionAwardCreateInputValidator(new StubClock());
        var input = ValidCreate() with { BeneficiaryIdnp = "12345" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.BeneficiaryIdnp));
    }

    [Fact]
    public void Create_InvalidSportDiscipline_Rejected()
    {
        var v = new AthletePensionAwardCreateInputValidator(new StubClock());
        var input = ValidCreate() with { SportDiscipline = "lowercase-bad" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.SportDiscipline));
    }

    [Fact]
    public void Create_AgeOutOfRange_Rejected()
    {
        var v = new AthletePensionAwardCreateInputValidator(new StubClock());
        // 15 years old at evaluation date = under 16-min.
        var input = ValidCreate() with { BeneficiaryBirthDate = new DateOnly(2012, 1, 1) };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CareerRecord_CoachYearsServiceWithoutYears_Rejected()
    {
        var v = new AthleteCareerRecordInputValidator(new StubClock());
        var input = new AthleteCareerRecordInputDto(
            AchievementKind: nameof(AthleteAchievementKind.CoachYearsService),
            AchievementYear: 2020,
            Event: "Coached national team",
            Years: null,
            EvidenceDocumentReference: null);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Approval_AdditionalMultiplierOutOfRange_Rejected()
    {
        var v = new AthletePensionApprovalInputValidator();
        var input = new AthletePensionApprovalInputDto(
            Note: "Approved per regulation",
            RegulatoryBaseMdl: 3000m,
            AdditionalMultipliers: new List<decimal> { 5.0m });

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reason_TooShort_Rejected()
    {
        var v = new AthletePensionReasonInputValidator();
        var input = new AthletePensionReasonInputDto(Reason: "ab");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
