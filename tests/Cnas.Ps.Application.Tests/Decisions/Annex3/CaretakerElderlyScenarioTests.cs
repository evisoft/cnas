using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.13-A — Indemnizație îngrijitor persoană
/// vârstnică. Verifies the caretaker-role gate, the elderly-age threshold,
/// the not-employed gate, and that the benefit is a fixed 1 300 MDL. This
/// passport has three eligibility rules, hence the additional tertiary-refusal
/// test.
/// </summary>
public class CaretakerElderlyScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "CARETAKER_ELDERLY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForElderly", "value": true,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-greater-than", "fact": "elderlyAgeYears", "value": 74,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_ELDERLY_TOO_YOUNG" },
        { "rule": "fact-equals", "fact": "caretakerNotEmployed", "value": true,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_CARETAKER_EMPLOYED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1300.00,
        "currency": "MDL"
      },
      "successCode": "CARETAKER_ELDERLY_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isCaretaker, int elderlyAge, bool caretakerNotEmployed)
        => new(new Dictionary<string, object?>
        {
            ["caretakerForElderly"] = isCaretaker,
            ["elderlyAgeYears"] = elderlyAge,
            ["caretakerNotEmployed"] = caretakerNotEmployed,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, elderlyAge: 80, caretakerNotEmployed: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CARETAKER_ELDERLY_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1300m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, elderlyAge: 80, caretakerNotEmployed: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CARETAKER_ELDERLY_INELIGIBLE_NOT_CARETAKER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, elderlyAge: 60, caretakerNotEmployed: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CARETAKER_ELDERLY_INELIGIBLE_ELDERLY_TOO_YOUNG");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, elderlyAge: 80, caretakerNotEmployed: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CARETAKER_ELDERLY_INELIGIBLE_CARETAKER_EMPLOYED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, elderlyAge: 60, caretakerNotEmployed: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CARETAKER_ELDERLY_INELIGIBLE_NOT_CARETAKER");
        result.Value.ReasonCodes.Should().Contain("CARETAKER_ELDERLY_INELIGIBLE_ELDERLY_TOO_YOUNG");
        result.Value.ReasonCodes.Should().Contain("CARETAKER_ELDERLY_INELIGIBLE_CARETAKER_EMPLOYED");
        result.Value.ReasonCodes.Should().NotContain("CARETAKER_ELDERLY_ELIGIBLE");
    }
}
