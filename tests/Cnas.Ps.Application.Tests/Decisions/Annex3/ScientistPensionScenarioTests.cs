using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.15-C — Pensie savant. Verifies the scientific-
/// degree gate, the 25-year research career threshold, and that the benefit is
/// a fixed 4 500 MDL.
/// </summary>
public class ScientistPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "SCIENTIST_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "holdsScientificDegree", "value": true,
          "failCode": "SCIENTIST_PENSION_INELIGIBLE_NO_DEGREE" },
        { "rule": "fact-greater-than", "fact": "researchCareerYears", "value": 24,
          "failCode": "SCIENTIST_PENSION_INELIGIBLE_CAREER_YEARS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4500.00,
        "currency": "MDL"
      },
      "successCode": "SCIENTIST_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool holdsDegree, int years)
        => new(new Dictionary<string, object?>
        {
            ["holdsScientificDegree"] = holdsDegree,
            ["researchCareerYears"] = years,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(holdsDegree: true, years: 30));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SCIENTIST_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(holdsDegree: false, years: 30));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SCIENTIST_PENSION_INELIGIBLE_NO_DEGREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(holdsDegree: true, years: 10));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SCIENTIST_PENSION_INELIGIBLE_CAREER_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(holdsDegree: false, years: 10));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SCIENTIST_PENSION_INELIGIBLE_NO_DEGREE");
        result.Value.ReasonCodes.Should().Contain("SCIENTIST_PENSION_INELIGIBLE_CAREER_YEARS");
        result.Value.ReasonCodes.Should().NotContain("SCIENTIST_PENSION_ELIGIBLE");
    }
}
