using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.11-A — Indemnizație pentru accident de muncă.
/// Verifies the commission-verification gate, the 30-day incapacity threshold,
/// and that the benefit is 100% of the average daily earnings.
/// </summary>
public class WorkInjuryCompensationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "WORK_INJURY_COMP",
      "eligibility": [
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "WORK_INJURY_COMP_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-greater-than", "fact": "incapacityDays", "value": 30,
          "failCode": "WORK_INJURY_COMP_INELIGIBLE_INCAPACITY_TOO_SHORT" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "averageDailyEarningsMdl"
      },
      "successCode": "WORK_INJURY_COMP_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool verified, int days, Money earnings)
        => new(new Dictionary<string, object?>
        {
            ["accidentVerifiedByCommission"] = verified,
            ["incapacityDays"] = days,
            ["averageDailyEarningsMdl"] = earnings,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(verified: true, days: 60, earnings: Money.Mdl(450m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_COMP_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(450m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(verified: false, days: 60, earnings: Money.Mdl(450m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_COMP_INELIGIBLE_NOT_VERIFIED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(verified: true, days: 10, earnings: Money.Mdl(450m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_COMP_INELIGIBLE_INCAPACITY_TOO_SHORT");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(verified: false, days: 10, earnings: Money.Mdl(450m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_COMP_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_COMP_INELIGIBLE_INCAPACITY_TOO_SHORT");
        result.Value.ReasonCodes.Should().NotContain("WORK_INJURY_COMP_ELIGIBLE");
    }
}
