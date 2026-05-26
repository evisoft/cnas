using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-E — Mijloc de transport pentru persoanele cu
/// dizabilități (Disability mobility vehicle). Verifies the severe-degree gate
/// and the medical-recommendation gate. The "amount" is a 0 MDL sentinel because
/// the entitlement is an in-kind asset; eligible decisions still surface the
/// 0 MDL amount so downstream workflows can detect approval.
/// </summary>
public class DisabilityMobilityVehicleScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Disability-Mobility-Vehicle ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "DISABILITY_MOBILITY_VEHICLE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "disabilityDegree", "value": "severe",
          "failCode": "DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_DEGREE" },
        { "rule": "fact-equals", "fact": "medicalRecommendation", "value": true,
          "failCode": "DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_NO_RECOMMENDATION" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "DISABILITY_MOBILITY_VEHICLE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(string disabilityDegree, bool medicalRecommendation)
        => new(new Dictionary<string, object?>
        {
            ["disabilityDegree"] = disabilityDegree,
            ["medicalRecommendation"] = medicalRecommendation,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(disabilityDegree: "severe", medicalRecommendation: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_MOBILITY_VEHICLE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(0m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(disabilityDegree: "medium", medicalRecommendation: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_DEGREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(disabilityDegree: "severe", medicalRecommendation: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_NO_RECOMMENDATION");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(disabilityDegree: "medium", medicalRecommendation: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_NO_RECOMMENDATION");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_MOBILITY_VEHICLE_ELIGIBLE");
    }
}
