using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.4-A — Tratament balneo-sanatorial
/// (Sanatorium / balneological treatment voucher). Verifies the insured-claimant
/// gate, the medical-recommendation gate, the 2-year cooldown, and that the
/// payload is a fixed 0 MDL marker (voucher kind — no monetary payout).
/// </summary>
public class SanatoriumTreatmentScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Sanatorium-Treatment ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure
    /// layer.
    /// </summary>
    private const string SanatoriumJson = """
    {
      "code": "SANATORIUM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "SANATORIUM_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "medicalRecommendationOnFile", "value": true,
          "failCode": "SANATORIUM_INELIGIBLE_NO_RECOMMENDATION" },
        { "rule": "fact-greater-than", "fact": "lastSanatoriumYears", "value": 2,
          "failCode": "SANATORIUM_INELIGIBLE_COOLDOWN" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "SANATORIUM_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, bool medicalRecommendation, int lastYears)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["medicalRecommendationOnFile"] = medicalRecommendation,
            ["lastSanatoriumYears"] = lastYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            SanatoriumJson,
            Facts(isInsured: true, medicalRecommendation: true, lastYears: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SANATORIUM_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(0m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode_NotInsured()
    {
        var result = Engine.Evaluate(
            SanatoriumJson,
            Facts(isInsured: false, medicalRecommendation: true, lastYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SANATORIUM_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode_NoRecommendation()
    {
        var result = Engine.Evaluate(
            SanatoriumJson,
            Facts(isInsured: true, medicalRecommendation: false, lastYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SANATORIUM_INELIGIBLE_NO_RECOMMENDATION");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_Cooldown()
    {
        var result = Engine.Evaluate(
            SanatoriumJson,
            Facts(isInsured: true, medicalRecommendation: true, lastYears: 1));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SANATORIUM_INELIGIBLE_COOLDOWN");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            SanatoriumJson,
            Facts(isInsured: false, medicalRecommendation: false, lastYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SANATORIUM_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("SANATORIUM_INELIGIBLE_NO_RECOMMENDATION");
        result.Value.ReasonCodes.Should().NotContain("SANATORIUM_ELIGIBLE");
    }
}
