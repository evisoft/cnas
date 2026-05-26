using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.11-B — Pensie pentru boală profesională.
/// Verifies the disease-verification gate, the allowed-degree set gate, and
/// that the benefit is a tier-table lookup keyed by disability degree.
/// </summary>
public class OccupationalDiseasePensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "OCC_DISEASE_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "occupationalDiseaseVerified", "value": true,
          "failCode": "OCC_DISEASE_PENSION_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "OCC_DISEASE_PENSION_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3500.00,
          "accentuated": 2500.00
        }
      },
      "successCode": "OCC_DISEASE_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool verified, string degree)
        => new(new Dictionary<string, object?>
        {
            ["occupationalDiseaseVerified"] = verified,
            ["disabilityDegree"] = degree,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(verified: true, degree: "severe"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OCC_DISEASE_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(verified: false, degree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OCC_DISEASE_PENSION_INELIGIBLE_NOT_VERIFIED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(verified: true, degree: "medium"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OCC_DISEASE_PENSION_INELIGIBLE_DEGREE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(verified: false, degree: "medium"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("OCC_DISEASE_PENSION_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().Contain("OCC_DISEASE_PENSION_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().NotContain("OCC_DISEASE_PENSION_ELIGIBLE");
    }
}
