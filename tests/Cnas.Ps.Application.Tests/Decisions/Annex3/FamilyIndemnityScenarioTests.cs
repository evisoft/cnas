using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.12-A — Indemnizație familială.
/// Verifies the household-size gate, the income threshold, and that the benefit
/// is a fixed 800 MDL.
/// </summary>
public class FamilyIndemnityScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "FAMILY_INDEMNITY",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 3,
          "failCode": "FAMILY_INDEMNITY_INELIGIBLE_HOUSEHOLD_TOO_SMALL" },
        { "rule": "fact-less-than", "fact": "currentMonthlyIncomeMdl", "value": 2500,
          "failCode": "FAMILY_INDEMNITY_INELIGIBLE_INCOME_TOO_HIGH" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 800.00,
        "currency": "MDL"
      },
      "successCode": "FAMILY_INDEMNITY_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(int householdSize, decimal income)
        => new(new Dictionary<string, object?>
        {
            ["householdSize"] = householdSize,
            ["currentMonthlyIncomeMdl"] = income,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 5, income: 1500m));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FAMILY_INDEMNITY_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(800m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 2, income: 1500m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FAMILY_INDEMNITY_INELIGIBLE_HOUSEHOLD_TOO_SMALL");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 5, income: 4000m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FAMILY_INDEMNITY_INELIGIBLE_INCOME_TOO_HIGH");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 2, income: 4000m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("FAMILY_INDEMNITY_INELIGIBLE_HOUSEHOLD_TOO_SMALL");
        result.Value.ReasonCodes.Should().Contain("FAMILY_INDEMNITY_INELIGIBLE_INCOME_TOO_HIGH");
        result.Value.ReasonCodes.Should().NotContain("FAMILY_INDEMNITY_ELIGIBLE");
    }
}
