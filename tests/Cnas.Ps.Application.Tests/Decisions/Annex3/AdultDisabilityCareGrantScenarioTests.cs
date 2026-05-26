using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-C — Indemnizație pentru îngrijirea adultului cu
/// dizabilitate (Adult-disability care grant). Verifies the caretaker-status
/// gate, the not-employed gate, and that the benefit is a flat 1 500 MDL.
/// </summary>
public class AdultDisabilityCareGrantScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Adult-Disability-Care ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "ADULT_DISABILITY_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledAdult", "value": true,
          "failCode": "ADULT_DISABILITY_CARE_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-equals", "fact": "caretakerNotEmployed", "value": true,
          "failCode": "ADULT_DISABILITY_CARE_INELIGIBLE_EMPLOYED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "ADULT_DISABILITY_CARE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isCaretaker, bool notEmployed)
        => new(new Dictionary<string, object?>
        {
            ["caretakerForDisabledAdult"] = isCaretaker,
            ["caretakerNotEmployed"] = notEmployed,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, notEmployed: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADULT_DISABILITY_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, notEmployed: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADULT_DISABILITY_CARE_INELIGIBLE_NOT_CARETAKER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: true, notEmployed: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADULT_DISABILITY_CARE_INELIGIBLE_EMPLOYED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isCaretaker: false, notEmployed: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("ADULT_DISABILITY_CARE_INELIGIBLE_NOT_CARETAKER");
        result.Value.ReasonCodes.Should().Contain("ADULT_DISABILITY_CARE_INELIGIBLE_EMPLOYED");
        result.Value.ReasonCodes.Should().NotContain("ADULT_DISABILITY_CARE_ELIGIBLE");
    }
}
