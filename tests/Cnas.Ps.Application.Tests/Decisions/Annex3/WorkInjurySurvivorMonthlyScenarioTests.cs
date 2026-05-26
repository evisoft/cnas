using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.11-D — Pensie lunară de urmaș (accident de
/// muncă). Verifies the work-accident cause-of-death gate, the recognized-
/// relationship set, and that the benefit is 100% of the deceased's average
/// insured income.
/// </summary>
public class WorkInjurySurvivorMonthlyScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "WORK_INJURY_SURVIVOR",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deathFromWorkAccident", "value": true,
          "failCode": "WORK_INJURY_SURVIVOR_INELIGIBLE_NOT_WORK_ACCIDENT" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "WORK_INJURY_SURVIVOR_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "WORK_INJURY_SURVIVOR_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool workAccident, string relationship, Money income)
        => new(new Dictionary<string, object?>
        {
            ["deathFromWorkAccident"] = workAccident,
            ["relationshipToDeceased"] = relationship,
            ["deceasedAverageInsuredIncomeMdl"] = income,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: true, relationship: "spouse", income: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_SURVIVOR_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(8000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: false, relationship: "spouse", income: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_SURVIVOR_INELIGIBLE_NOT_WORK_ACCIDENT");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: true, relationship: "uncle", income: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_SURVIVOR_INELIGIBLE_RELATIONSHIP");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: false, relationship: "uncle", income: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_SURVIVOR_INELIGIBLE_NOT_WORK_ACCIDENT");
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_SURVIVOR_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().NotContain("WORK_INJURY_SURVIVOR_ELIGIBLE");
    }
}
