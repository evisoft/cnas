using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-G — Alocație pentru transport pentru persoanele
/// cu dizabilități (Transport allowance for disabled persons). Verifies the
/// disability-degree gate, the medical-recommendation gate, and that the benefit
/// is a flat 800 MDL.
/// </summary>
public class TransportAllowanceDisabledScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Transport-Allowance-Disabled ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "TRANSPORT_ALLOW_DISABLED",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "TRANSPORT_ALLOW_DISABLED_INELIGIBLE_DEGREE" },
        { "rule": "fact-equals", "fact": "medicalRecommendation", "value": true,
          "failCode": "TRANSPORT_ALLOW_DISABLED_INELIGIBLE_NO_RECOMMENDATION" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 800.00,
        "currency": "MDL"
      },
      "successCode": "TRANSPORT_ALLOW_DISABLED_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(string degree, bool recommendation)
        => new(new Dictionary<string, object?>
        {
            ["disabilityDegree"] = degree,
            ["medicalRecommendation"] = recommendation,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(degree: "severe", recommendation: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRANSPORT_ALLOW_DISABLED_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(800m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(degree: "medium", recommendation: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRANSPORT_ALLOW_DISABLED_INELIGIBLE_DEGREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(degree: "accentuated", recommendation: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRANSPORT_ALLOW_DISABLED_INELIGIBLE_NO_RECOMMENDATION");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(degree: "none", recommendation: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("TRANSPORT_ALLOW_DISABLED_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().Contain("TRANSPORT_ALLOW_DISABLED_INELIGIBLE_NO_RECOMMENDATION");
        result.Value.ReasonCodes.Should().NotContain("TRANSPORT_ALLOW_DISABLED_ELIGIBLE");
    }
}
