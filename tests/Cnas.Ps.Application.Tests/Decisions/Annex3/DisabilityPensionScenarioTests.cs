using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-A — Pensie de dizabilitate (Disability pension).
/// Verifies that eligibility requires both a recognized disability degree and the
/// 12-month minimum contribution stage, and that the benefit amount is keyed by
/// disability degree.
/// </summary>
public class DisabilityPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Disability Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string DisabilityPensionJson = """
    {
      "code": "DISABILITY_PENSION",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "DISABILITY_PENSION_INELIGIBLE_DEGREE" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 11,
          "failCode": "DISABILITY_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      2500.00,
          "accentuated": 1800.00,
          "medium":      1200.00
        }
      },
      "successCode": "DISABILITY_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(string disabilityDegree, int contributionMonths)
        => new(new Dictionary<string, object?>
        {
            ["disabilityDegree"] = disabilityDegree,
            ["contributionMonths"] = contributionMonths,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            DisabilityPensionJson,
            Facts(disabilityDegree: "severe", contributionMonths: 24));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2500m));
    }

    [Fact]
    public void Happy_Path_MediumDegree_LooksUpLowerTier()
    {
        var result = Engine.Evaluate(
            DisabilityPensionJson,
            Facts(disabilityDegree: "medium", contributionMonths: 24));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(1200m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            DisabilityPensionJson,
            Facts(disabilityDegree: "none", contributionMonths: 24));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PENSION_INELIGIBLE_DEGREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            DisabilityPensionJson,
            Facts(disabilityDegree: "severe", contributionMonths: 6));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PENSION_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            DisabilityPensionJson,
            Facts(disabilityDegree: "none", contributionMonths: 6));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_PENSION_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_PENSION_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_PENSION_ELIGIBLE");
    }
}
