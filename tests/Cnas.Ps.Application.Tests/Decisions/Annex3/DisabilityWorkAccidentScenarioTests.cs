using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-B — Pensie de dizabilitate (accident de muncă)
/// (Work-accident disability pension). Verifies the insured-claimant gate, the
/// commission-verification gate, the recognized disability-degree gate, and that
/// the benefit is a tier-table lookup keyed by disability degree.
/// </summary>
public class DisabilityWorkAccidentScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Work-Accident-Disability-Pension ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row by the
    /// infrastructure layer.
    /// </summary>
    private const string DisabilityWorkAccidentJson = """
    {
      "code": "DISABILITY_WORK_ACCIDENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3000.00,
          "accentuated": 2200.00,
          "medium":      1500.00
        }
      },
      "successCode": "DISABILITY_WORK_ACCIDENT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, bool accidentVerified, string disabilityDegree)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["accidentVerifiedByCommission"] = accidentVerified,
            ["disabilityDegree"] = disabilityDegree,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            DisabilityWorkAccidentJson,
            Facts(isInsured: true, accidentVerified: true, disabilityDegree: "severe"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_WORK_ACCIDENT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            DisabilityWorkAccidentJson,
            Facts(isInsured: false, accidentVerified: true, disabilityDegree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode_NotVerified()
    {
        var result = Engine.Evaluate(
            DisabilityWorkAccidentJson,
            Facts(isInsured: true, accidentVerified: false, disabilityDegree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_UnknownDegree()
    {
        var result = Engine.Evaluate(
            DisabilityWorkAccidentJson,
            Facts(isInsured: true, accidentVerified: true, disabilityDegree: "none"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_WORK_ACCIDENT_INELIGIBLE_DEGREE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            DisabilityWorkAccidentJson,
            Facts(isInsured: false, accidentVerified: false, disabilityDegree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_WORK_ACCIDENT_ELIGIBLE");
    }
}
