using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.9-A — Pensie pentru victimele Cernobîl (Chernobyl-
/// victim pension). Verifies the victim-status gate, the commission-verification
/// gate, and that the benefit is a flat 3 500 MDL.
/// </summary>
public class ChernobylVictimPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Chernobyl-Victim ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "CHERNOBYL_VICTIM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isChernobylVictim", "value": true,
          "failCode": "CHERNOBYL_VICTIM_INELIGIBLE_NOT_VICTIM" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "CHERNOBYL_VICTIM_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3500.00,
        "currency": "MDL"
      },
      "successCode": "CHERNOBYL_VICTIM_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isVictim, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["isChernobylVictim"] = isVictim,
            ["verifiedByCommission"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isVictim: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHERNOBYL_VICTIM_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVictim: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHERNOBYL_VICTIM_INELIGIBLE_NOT_VICTIM");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVictim: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHERNOBYL_VICTIM_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isVictim: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHERNOBYL_VICTIM_INELIGIBLE_NOT_VICTIM");
        result.Value.ReasonCodes.Should().Contain("CHERNOBYL_VICTIM_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("CHERNOBYL_VICTIM_ELIGIBLE");
    }
}
