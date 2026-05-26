using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-D — Alocație pentru nevăzători (Blind allowance).
/// Verifies the legal-blindness gate, the medical-commission verification gate,
/// and that the benefit is a flat 1 800 MDL.
/// </summary>
public class BlindAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Blind-Allowance ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "BLIND_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isLegallyBlind", "value": true,
          "failCode": "BLIND_ALLOWANCE_INELIGIBLE_NOT_BLIND" },
        { "rule": "fact-equals", "fact": "medicalCommissionVerified", "value": true,
          "failCode": "BLIND_ALLOWANCE_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1800.00,
        "currency": "MDL"
      },
      "successCode": "BLIND_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isBlind, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["isLegallyBlind"] = isBlind,
            ["medicalCommissionVerified"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isBlind: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("BLIND_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1800m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isBlind: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("BLIND_ALLOWANCE_INELIGIBLE_NOT_BLIND");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isBlind: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("BLIND_ALLOWANCE_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isBlind: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("BLIND_ALLOWANCE_INELIGIBLE_NOT_BLIND");
        result.Value.ReasonCodes.Should().Contain("BLIND_ALLOWANCE_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("BLIND_ALLOWANCE_ELIGIBLE");
    }
}
