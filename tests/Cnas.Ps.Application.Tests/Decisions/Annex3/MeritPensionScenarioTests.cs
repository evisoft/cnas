using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-E — Pensie de merit (Merit pension). Verifies
/// the merit-category gate, the 20-year contribution threshold, and that the
/// benefit is a fixed 4 000 MDL.
/// </summary>
public class MeritPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Merit Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "MERIT_PENSION",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "meritCategory",
          "values": ["culture", "science", "sport", "labor"],
          "failCode": "MERIT_PENSION_INELIGIBLE_CATEGORY" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 19,
          "failCode": "MERIT_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4000.00,
        "currency": "MDL"
      },
      "successCode": "MERIT_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(string meritCategory, int contributionYears)
        => new(new Dictionary<string, object?>
        {
            ["meritCategory"] = meritCategory,
            ["contributionYears"] = contributionYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(meritCategory: "science", contributionYears: 30));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MERIT_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(meritCategory: "marketing", contributionYears: 30));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MERIT_PENSION_INELIGIBLE_CATEGORY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(meritCategory: "labor", contributionYears: 10));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MERIT_PENSION_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(meritCategory: "marketing", contributionYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("MERIT_PENSION_INELIGIBLE_CATEGORY");
        result.Value.ReasonCodes.Should().Contain("MERIT_PENSION_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("MERIT_PENSION_ELIGIBLE");
    }
}
