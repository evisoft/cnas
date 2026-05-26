using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-H — Indemnizație lunară pentru invalizii de
/// război (War-invalid monthly allowance). Verifies the veteran-status gate, the
/// disability-degree gate, and that the benefit amount is keyed by severity.
/// </summary>
public class WarInvalidMonthlyAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical War-Invalid-Monthly ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "WAR_INVALID_MONTHLY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "WAR_INVALID_MONTHLY_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "WAR_INVALID_MONTHLY_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      4000.00,
          "accentuated": 3000.00,
          "medium":      2200.00
        }
      },
      "successCode": "WAR_INVALID_MONTHLY_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isVeteran, string degree)
        => new(new Dictionary<string, object?>
        {
            ["isWarVeteran"] = isVeteran,
            ["disabilityDegree"] = degree,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: true, degree: "severe"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_INVALID_MONTHLY_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: false, degree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_INVALID_MONTHLY_INELIGIBLE_NOT_VETERAN");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: true, degree: "none"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_INVALID_MONTHLY_INELIGIBLE_DEGREE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: false, degree: "none"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WAR_INVALID_MONTHLY_INELIGIBLE_NOT_VETERAN");
        result.Value.ReasonCodes.Should().Contain("WAR_INVALID_MONTHLY_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().NotContain("WAR_INVALID_MONTHLY_ELIGIBLE");
    }
}
