using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0141 / TOR CF 15.03 — pins the validator contract on
/// <see cref="BusinessRuleInputValidator"/>. Each test isolates one branch
/// so a regression in field bounds, JSON-shape gating, or enum membership
/// fails its dedicated row.
/// </summary>
public sealed class BusinessRuleInputValidatorTests
{
    private static BusinessRuleInputDto Good() => new(
        Id: null,
        Name: "Reject minors",
        ApplicantType: BusinessRuleApplicantType.Natural,
        ConditionJson: """{"rule":"fact-less-than","fact":"ageYears","value":18}""",
        DecisionOutcome: BusinessRuleDecisionOutcome.Rejected,
        Notes: "Source: Legea 289/2004 §3.2");

    [Fact]
    public void HappyPath_Accepted()
    {
        var v = new BusinessRuleInputValidator();
        v.Validate(Good()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyName_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        v.Validate(Good() with { Name = string.Empty }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void NameTooShort_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        v.Validate(Good() with { Name = "ab" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void MalformedConditionJson_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        var result = v.Validate(Good() with { ConditionJson = "{not-json" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("well-formed JSON", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OutOfRangeApplicantType_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        // Cast a deliberately out-of-range value through the enum.
        var input = Good() with { ApplicantType = (BusinessRuleApplicantType)42 };
        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void OutOfRangeDecisionOutcome_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        var input = Good() with { DecisionOutcome = (BusinessRuleDecisionOutcome)99 };
        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void NotesTooLong_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        var input = Good() with { Notes = new string('x', 2001) };
        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EmptyConditionJson_Rejected()
    {
        var v = new BusinessRuleInputValidator();
        v.Validate(Good() with { ConditionJson = "  " }).IsValid.Should().BeFalse();
    }
}
