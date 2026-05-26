using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-F — Alocație pentru utilizatori de scaun cu
/// rotile (Wheelchair-user allowance). Verifies the wheelchair-use status gate,
/// the medical-commission verification gate, and that the benefit is a flat
/// 2 200 MDL.
/// </summary>
public class WheelchairUserAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Wheelchair-User ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "WHEELCHAIR_USER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "usesWheelchair", "value": true,
          "failCode": "WHEELCHAIR_USER_INELIGIBLE_NOT_WHEELCHAIR_USER" },
        { "rule": "fact-equals", "fact": "medicalCommissionVerified", "value": true,
          "failCode": "WHEELCHAIR_USER_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2200.00,
        "currency": "MDL"
      },
      "successCode": "WHEELCHAIR_USER_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool usesWheelchair, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["usesWheelchair"] = usesWheelchair,
            ["medicalCommissionVerified"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(usesWheelchair: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WHEELCHAIR_USER_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2200m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(usesWheelchair: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WHEELCHAIR_USER_INELIGIBLE_NOT_WHEELCHAIR_USER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(usesWheelchair: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WHEELCHAIR_USER_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(usesWheelchair: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WHEELCHAIR_USER_INELIGIBLE_NOT_WHEELCHAIR_USER");
        result.Value.ReasonCodes.Should().Contain("WHEELCHAIR_USER_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("WHEELCHAIR_USER_ELIGIBLE");
    }
}
