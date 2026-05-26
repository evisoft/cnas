using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.12-B — Alocație pentru familii numeroase.
/// Verifies the household-size gate, the vulnerability-certification gate,
/// and that the benefit is a fixed 1 500 MDL.
/// </summary>
public class LargeFamilyAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LARGE_FAMILY",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 4,
          "failCode": "LARGE_FAMILY_INELIGIBLE_HOUSEHOLD_TOO_SMALL" },
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "LARGE_FAMILY_INELIGIBLE_NOT_VULNERABLE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "LARGE_FAMILY_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(int householdSize, bool vulnerable)
        => new(new Dictionary<string, object?>
        {
            ["householdSize"] = householdSize,
            ["householdCertifiedVulnerable"] = vulnerable,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 6, vulnerable: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LARGE_FAMILY_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 3, vulnerable: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LARGE_FAMILY_INELIGIBLE_HOUSEHOLD_TOO_SMALL");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 6, vulnerable: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LARGE_FAMILY_INELIGIBLE_NOT_VULNERABLE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(householdSize: 3, vulnerable: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LARGE_FAMILY_INELIGIBLE_HOUSEHOLD_TOO_SMALL");
        result.Value.ReasonCodes.Should().Contain("LARGE_FAMILY_INELIGIBLE_NOT_VULNERABLE");
        result.Value.ReasonCodes.Should().NotContain("LARGE_FAMILY_ELIGIBLE");
    }
}
