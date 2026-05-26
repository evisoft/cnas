using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.13-C — Indemnizație respiro îngrijitor
/// dizabilitate. Verifies the caretaker gate, the 90-day care-days threshold,
/// and that the benefit is a fixed 600 MDL.
/// </summary>
public class RespiteCareDisabilityScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "RESPITE_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledRelative", "value": true,
          "failCode": "RESPITE_CARE_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-greater-than", "fact": "careDays", "value": 89,
          "failCode": "RESPITE_CARE_INELIGIBLE_INSUFFICIENT_CARE_DAYS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 600.00,
        "currency": "MDL"
      },
      "successCode": "RESPITE_CARE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isCaretaker, int careDays)
        => new(new Dictionary<string, object?>
        {
            ["caretakerForDisabledRelative"] = isCaretaker,
            ["careDays"] = careDays,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, careDays: 100));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("RESPITE_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(600m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, careDays: 100));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("RESPITE_CARE_INELIGIBLE_NOT_CARETAKER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, careDays: 30));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("RESPITE_CARE_INELIGIBLE_INSUFFICIENT_CARE_DAYS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, careDays: 30));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("RESPITE_CARE_INELIGIBLE_NOT_CARETAKER");
        result.Value.ReasonCodes.Should().Contain("RESPITE_CARE_INELIGIBLE_INSUFFICIENT_CARE_DAYS");
        result.Value.ReasonCodes.Should().NotContain("RESPITE_CARE_ELIGIBLE");
    }
}
