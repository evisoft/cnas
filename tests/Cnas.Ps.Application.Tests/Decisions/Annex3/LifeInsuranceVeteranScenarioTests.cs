using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.14-A — Asigurare de viață veteran. Verifies the
/// veteran-status gate, the commission-verification gate, and that the benefit
/// is a fixed 5 000 MDL.
/// </summary>
public class LifeInsuranceVeteranScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LIFE_INS_VETERAN",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "LIFE_INS_VETERAN_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "LIFE_INS_VETERAN_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 5000.00,
        "currency": "MDL"
      },
      "successCode": "LIFE_INS_VETERAN_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isVeteran, bool verified)
        => new(new Dictionary<string, object?>
        {
            ["isWarVeteran"] = isVeteran,
            ["verifiedByCommission"] = verified,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: true, verified: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIFE_INS_VETERAN_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(5000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: false, verified: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIFE_INS_VETERAN_INELIGIBLE_NOT_VETERAN");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: true, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LIFE_INS_VETERAN_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isVeteran: false, verified: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LIFE_INS_VETERAN_INELIGIBLE_NOT_VETERAN");
        result.Value.ReasonCodes.Should().Contain("LIFE_INS_VETERAN_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("LIFE_INS_VETERAN_ELIGIBLE");
    }
}
