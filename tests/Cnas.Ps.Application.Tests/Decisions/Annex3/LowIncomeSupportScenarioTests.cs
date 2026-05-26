using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-E — Ajutor pentru venit mic (Low-income
/// support). Verifies the income-below-threshold gate, the multi-person household
/// gate, and that the benefit is a flat 600 MDL.
/// </summary>
public class LowIncomeSupportScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Low-Income-Support ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "LOW_INCOME_SUPPORT",
      "eligibility": [
        { "rule": "fact-less-than", "fact": "currentMonthlyIncomeMdl", "value": 1200,
          "failCode": "LOW_INCOME_SUPPORT_INELIGIBLE_INCOME_ABOVE_THRESHOLD" },
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 1,
          "failCode": "LOW_INCOME_SUPPORT_INELIGIBLE_HOUSEHOLD_SIZE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 600.00,
        "currency": "MDL"
      },
      "successCode": "LOW_INCOME_SUPPORT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(decimal income, int householdSize)
        => new(new Dictionary<string, object?>
        {
            ["currentMonthlyIncomeMdl"] = income,
            ["householdSize"] = householdSize,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(income: 800m, householdSize: 3));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LOW_INCOME_SUPPORT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(600m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(income: 2000m, householdSize: 3));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LOW_INCOME_SUPPORT_INELIGIBLE_INCOME_ABOVE_THRESHOLD");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(income: 800m, householdSize: 1));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LOW_INCOME_SUPPORT_INELIGIBLE_HOUSEHOLD_SIZE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(income: 2000m, householdSize: 1));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LOW_INCOME_SUPPORT_INELIGIBLE_INCOME_ABOVE_THRESHOLD");
        result.Value.ReasonCodes.Should().Contain("LOW_INCOME_SUPPORT_INELIGIBLE_HOUSEHOLD_SIZE");
        result.Value.ReasonCodes.Should().NotContain("LOW_INCOME_SUPPORT_ELIGIBLE");
    }
}
