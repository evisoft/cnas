using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-B — Indemnizație pentru îngrijitorul copilului
/// cu dizabilitate (Child-caretaker allowance). Verifies the caretaker-status
/// gate, the caretaker-insured gate, and that the benefit is a flat 2 000 MDL.
/// </summary>
public class ChildCaretakerAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Child-Caretaker ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "CHILD_CARETAKER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledChild", "value": true,
          "failCode": "CHILD_CARETAKER_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-equals", "fact": "caretakerInsured", "value": true,
          "failCode": "CHILD_CARETAKER_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2000.00,
        "currency": "MDL"
      },
      "successCode": "CHILD_CARETAKER_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isCaretaker, bool insured)
        => new(new Dictionary<string, object?>
        {
            ["caretakerForDisabledChild"] = isCaretaker,
            ["caretakerInsured"] = insured,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, insured: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARETAKER_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, insured: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARETAKER_INELIGIBLE_NOT_CARETAKER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, insured: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARETAKER_INELIGIBLE_NOT_INSURED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, insured: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHILD_CARETAKER_INELIGIBLE_NOT_CARETAKER");
        result.Value.ReasonCodes.Should().Contain("CHILD_CARETAKER_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().NotContain("CHILD_CARETAKER_ELIGIBLE");
    }
}
