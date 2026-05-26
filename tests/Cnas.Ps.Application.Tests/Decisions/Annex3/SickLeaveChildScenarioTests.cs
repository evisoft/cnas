using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.1-D — Indemnizație pentru îngrijirea copilului bolnav
/// (Sick-leave allowance for child care). Verifies the insured-parent gate, the
/// under-15-years child-age cap, and that the benefit is 90% of the daily salary.
/// </summary>
public class SickLeaveChildScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Sick-Leave-for-Child ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string SickLeaveChildJson = """
    {
      "code": "SICK_LEAVE_CHILD",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "SICK_LEAVE_CHILD_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 15,
          "failCode": "SICK_LEAVE_CHILD_INELIGIBLE_CHILD_TOO_OLD" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 90,
        "referenceFact": "dailySalaryMdl"
      },
      "successCode": "SICK_LEAVE_CHILD_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, int childAgeYears, Money dailySalary)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["childAgeYears"] = childAgeYears,
            ["dailySalaryMdl"] = dailySalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            SickLeaveChildJson,
            Facts(isInsured: true, childAgeYears: 7, dailySalary: Money.Mdl(500m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SICK_LEAVE_CHILD_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(450m)); // 90% of 500
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            SickLeaveChildJson,
            Facts(isInsured: false, childAgeYears: 7, dailySalary: Money.Mdl(500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SICK_LEAVE_CHILD_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            SickLeaveChildJson,
            Facts(isInsured: true, childAgeYears: 16, dailySalary: Money.Mdl(500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SICK_LEAVE_CHILD_INELIGIBLE_CHILD_TOO_OLD");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            SickLeaveChildJson,
            Facts(isInsured: false, childAgeYears: 16, dailySalary: Money.Mdl(500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SICK_LEAVE_CHILD_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("SICK_LEAVE_CHILD_INELIGIBLE_CHILD_TOO_OLD");
        result.Value.ReasonCodes.Should().NotContain("SICK_LEAVE_CHILD_ELIGIBLE");
    }
}
