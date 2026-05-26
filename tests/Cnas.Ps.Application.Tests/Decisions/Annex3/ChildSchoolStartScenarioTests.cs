using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.12-E — Ajutor de început de an școlar.
/// Verifies the child-age gate, the enrolment gate, the vulnerability gate,
/// and that the benefit is a fixed 500 MDL. This passport has three
/// eligibility rules, hence the additional tertiary-refusal test.
/// </summary>
public class ChildSchoolStartScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "CHILD_SCHOOL_START",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "childAgeYears", "value": 5,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_TOO_YOUNG" },
        { "rule": "fact-equals", "fact": "childEnrolledInSchool", "value": true,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_NOT_ENROLLED" },
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_NOT_VULNERABLE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 500.00,
        "currency": "MDL"
      },
      "successCode": "CHILD_SCHOOL_START_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(int age, bool enrolled, bool vulnerable)
        => new(new Dictionary<string, object?>
        {
            ["childAgeYears"] = age,
            ["childEnrolledInSchool"] = enrolled,
            ["householdCertifiedVulnerable"] = vulnerable,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(age: 7, enrolled: true, vulnerable: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_SCHOOL_START_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(age: 4, enrolled: true, vulnerable: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_SCHOOL_START_INELIGIBLE_TOO_YOUNG");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(age: 7, enrolled: false, vulnerable: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_SCHOOL_START_INELIGIBLE_NOT_ENROLLED");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(age: 7, enrolled: true, vulnerable: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_SCHOOL_START_INELIGIBLE_NOT_VULNERABLE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(age: 4, enrolled: false, vulnerable: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHILD_SCHOOL_START_INELIGIBLE_TOO_YOUNG");
        result.Value.ReasonCodes.Should().Contain("CHILD_SCHOOL_START_INELIGIBLE_NOT_ENROLLED");
        result.Value.ReasonCodes.Should().Contain("CHILD_SCHOOL_START_INELIGIBLE_NOT_VULNERABLE");
        result.Value.ReasonCodes.Should().NotContain("CHILD_SCHOOL_START_ELIGIBLE");
    }
}
