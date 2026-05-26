using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.9-C — Pensie pentru lichidatorii avariei de la
/// Cernobîl (Chernobyl liquidator pension). Verifies the liquidator-status gate,
/// the commission-verification gate, and that the benefit is a fixed 4 000 MDL.
/// </summary>
public class LiquidatorPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Liquidator-Pension ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "LIQUIDATOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasChernobylLiquidator", "value": true,
          "failCode": "LIQUIDATOR_PENSION_INELIGIBLE_NOT_LIQUIDATOR" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "LIQUIDATOR_PENSION_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4000.00,
        "currency": "MDL"
      },
      "successCode": "LIQUIDATOR_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasLiquidator, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["wasChernobylLiquidator"] = wasLiquidator,
            ["verifiedByCommission"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasLiquidator: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIQUIDATOR_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasLiquidator: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIQUIDATOR_PENSION_INELIGIBLE_NOT_LIQUIDATOR");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasLiquidator: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIQUIDATOR_PENSION_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasLiquidator: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LIQUIDATOR_PENSION_INELIGIBLE_NOT_LIQUIDATOR");
        result.Value.ReasonCodes.Should().Contain("LIQUIDATOR_PENSION_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("LIQUIDATOR_PENSION_ELIGIBLE");
    }
}
