using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.4-C — Compensarea tratamentului ambulatoriu
/// (Ambulatory treatment compensation). Verifies the insurance gate, the
/// commission-approval gate, and that the benefit is 70% of the documented cost.
/// </summary>
public class AmbulatoryTreatmentScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Ambulatory-Treatment ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "AMBULATORY_TREATMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "AMBULATORY_TREATMENT_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "treatmentApprovedByCommission", "value": true,
          "failCode": "AMBULATORY_TREATMENT_INELIGIBLE_NOT_APPROVED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "treatmentCostMdl"
      },
      "successCode": "AMBULATORY_TREATMENT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, bool treatmentApproved, Money treatmentCost)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["treatmentApprovedByCommission"] = treatmentApproved,
            ["treatmentCostMdl"] = treatmentCost,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isInsured: true, treatmentApproved: true, treatmentCost: Money.Mdl(1000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("AMBULATORY_TREATMENT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(700m)); // 70% of 1000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isInsured: false, treatmentApproved: true, treatmentCost: Money.Mdl(1000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("AMBULATORY_TREATMENT_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isInsured: true, treatmentApproved: false, treatmentCost: Money.Mdl(1000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("AMBULATORY_TREATMENT_INELIGIBLE_NOT_APPROVED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isInsured: false, treatmentApproved: false, treatmentCost: Money.Mdl(1000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("AMBULATORY_TREATMENT_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("AMBULATORY_TREATMENT_INELIGIBLE_NOT_APPROVED");
        result.Value.ReasonCodes.Should().NotContain("AMBULATORY_TREATMENT_ELIGIBLE");
    }
}
