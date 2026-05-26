using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.16-B — Indemnizație victimă a traficului.
/// Verifies the recognized-victim gate, the commission-verification gate, and
/// that the benefit is a fixed 6 000 MDL.
/// </summary>
public class VictimOfTraffickingScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "TRAFFICKING_VICTIM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "recognizedTraffickingVictim", "value": true,
          "failCode": "TRAFFICKING_VICTIM_INELIGIBLE_NOT_RECOGNIZED" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "TRAFFICKING_VICTIM_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 6000.00,
        "currency": "MDL"
      },
      "successCode": "TRAFFICKING_VICTIM_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool recognized, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["recognizedTraffickingVictim"] = recognized,
            ["verifiedByCommission"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(recognized: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRAFFICKING_VICTIM_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(recognized: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRAFFICKING_VICTIM_INELIGIBLE_NOT_RECOGNIZED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(recognized: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("TRAFFICKING_VICTIM_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(recognized: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("TRAFFICKING_VICTIM_INELIGIBLE_NOT_RECOGNIZED");
        result.Value.ReasonCodes.Should().Contain("TRAFFICKING_VICTIM_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("TRAFFICKING_VICTIM_ELIGIBLE");
    }
}
