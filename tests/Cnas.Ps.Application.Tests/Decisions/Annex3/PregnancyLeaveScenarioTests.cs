using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.1-E — Indemnizație de maternitate (sarcină și lăuzie)
/// (Pregnancy and maternity-leave allowance). Verifies the insured-claimant gate,
/// the 6-month minimum contribution stage, and that the benefit is 100% of the
/// average insured income.
/// </summary>
public class PregnancyLeaveScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Pregnancy-Leave ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string PregnancyLeaveJson = """
    {
      "code": "PREGNANCY_LEAVE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "PREGNANCY_LEAVE_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 5,
          "failCode": "PREGNANCY_LEAVE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "PREGNANCY_LEAVE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, int contributionMonths, Money averageIncome)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["contributionMonths"] = contributionMonths,
            ["averageInsuredIncomeMdl"] = averageIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            PregnancyLeaveJson,
            Facts(isInsured: true, contributionMonths: 18, averageIncome: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PREGNANCY_LEAVE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(8000m)); // 100% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            PregnancyLeaveJson,
            Facts(isInsured: false, contributionMonths: 18, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PREGNANCY_LEAVE_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            PregnancyLeaveJson,
            Facts(isInsured: true, contributionMonths: 3, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PREGNANCY_LEAVE_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            PregnancyLeaveJson,
            Facts(isInsured: false, contributionMonths: 3, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PREGNANCY_LEAVE_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("PREGNANCY_LEAVE_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("PREGNANCY_LEAVE_ELIGIBLE");
    }
}
