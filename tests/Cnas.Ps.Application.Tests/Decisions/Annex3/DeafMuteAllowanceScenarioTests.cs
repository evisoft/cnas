using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-E — Alocație pentru surdomuți (Deaf-mute
/// allowance). Verifies the deaf-mute status gate, the medical-commission
/// verification gate, and that the benefit is a flat 1 600 MDL.
/// </summary>
public class DeafMuteAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Deaf-Mute-Allowance ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "DEAF_MUTE_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isDeafMute", "value": true,
          "failCode": "DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_DEAFMUTE" },
        { "rule": "fact-equals", "fact": "medicalCommissionVerified", "value": true,
          "failCode": "DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1600.00,
        "currency": "MDL"
      },
      "successCode": "DEAF_MUTE_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isDeafMute, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["isDeafMute"] = isDeafMute,
            ["medicalCommissionVerified"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isDeafMute: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEAF_MUTE_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1600m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isDeafMute: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_DEAFMUTE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isDeafMute: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isDeafMute: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_DEAFMUTE");
        result.Value.ReasonCodes.Should().Contain("DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("DEAF_MUTE_ALLOWANCE_ELIGIBLE");
    }
}
