using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-C — Reevaluare a gradului de dizabilitate
/// (Disability reassessment). Verifies the existing-record gate, the
/// reassessment-due gate (a boolean fact derived upstream because the current
/// engine lacks a "date-older-than-N-days" rule kind), and the insured-claimant
/// gate. The payload is a fixed 0 MDL marker (decision only — no payout).
/// </summary>
public class DisabilityReassessmentScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Disability-Reassessment ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row by the
    /// infrastructure layer.
    /// </summary>
    private const string DisabilityReassessmentJson = """
    {
      "code": "DISABILITY_REASSESSMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "existingDisabilityRecord", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NO_RECORD" },
        { "rule": "fact-equals", "fact": "reassessmentDue", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NOT_DUE" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "DISABILITY_REASSESSMENT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool existingRecord, bool reassessmentDue, bool isInsured)
        => new(new Dictionary<string, object?>
        {
            ["existingDisabilityRecord"] = existingRecord,
            ["reassessmentDue"] = reassessmentDue,
            ["isInsured"] = isInsured,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            DisabilityReassessmentJson,
            Facts(existingRecord: true, reassessmentDue: true, isInsured: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_REASSESSMENT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(0m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode_NoRecord()
    {
        var result = Engine.Evaluate(
            DisabilityReassessmentJson,
            Facts(existingRecord: false, reassessmentDue: true, isInsured: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_REASSESSMENT_INELIGIBLE_NO_RECORD");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode_NotDue()
    {
        var result = Engine.Evaluate(
            DisabilityReassessmentJson,
            Facts(existingRecord: true, reassessmentDue: false, isInsured: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_REASSESSMENT_INELIGIBLE_NOT_DUE");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_NotInsured()
    {
        var result = Engine.Evaluate(
            DisabilityReassessmentJson,
            Facts(existingRecord: true, reassessmentDue: true, isInsured: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_REASSESSMENT_INELIGIBLE_NOT_INSURED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            DisabilityReassessmentJson,
            Facts(existingRecord: false, reassessmentDue: false, isInsured: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_REASSESSMENT_INELIGIBLE_NO_RECORD");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_REASSESSMENT_INELIGIBLE_NOT_DUE");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_REASSESSMENT_ELIGIBLE");
    }
}
