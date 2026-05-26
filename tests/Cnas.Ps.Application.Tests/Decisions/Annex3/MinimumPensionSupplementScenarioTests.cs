using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-F — Supliment la pensia minimă (Minimum pension
/// supplement). Verifies the retiree-status gate, the below-1500-MDL threshold,
/// and that the benefit is a fixed 500 MDL supplement.
/// </summary>
public class MinimumPensionSupplementScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Minimum-Pension-Supplement ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "MIN_PENSION_SUPPLEMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isRetiree", "value": true,
          "failCode": "MIN_PENSION_SUPPLEMENT_INELIGIBLE_NOT_RETIREE" },
        { "rule": "fact-less-than", "fact": "currentPensionMdl", "value": 1500,
          "failCode": "MIN_PENSION_SUPPLEMENT_INELIGIBLE_ABOVE_THRESHOLD" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 500.00,
        "currency": "MDL"
      },
      "successCode": "MIN_PENSION_SUPPLEMENT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isRetiree, decimal currentPensionMdl)
        => new(new Dictionary<string, object?>
        {
            ["isRetiree"] = isRetiree,
            ["currentPensionMdl"] = currentPensionMdl,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isRetiree: true, currentPensionMdl: 1200m));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MIN_PENSION_SUPPLEMENT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isRetiree: false, currentPensionMdl: 1200m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MIN_PENSION_SUPPLEMENT_INELIGIBLE_NOT_RETIREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isRetiree: true, currentPensionMdl: 2500m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MIN_PENSION_SUPPLEMENT_INELIGIBLE_ABOVE_THRESHOLD");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isRetiree: false, currentPensionMdl: 2500m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("MIN_PENSION_SUPPLEMENT_INELIGIBLE_NOT_RETIREE");
        result.Value.ReasonCodes.Should().Contain("MIN_PENSION_SUPPLEMENT_INELIGIBLE_ABOVE_THRESHOLD");
        result.Value.ReasonCodes.Should().NotContain("MIN_PENSION_SUPPLEMENT_ELIGIBLE");
    }
}
