using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.8-A — Alocație pentru copii cu dizabilitate
/// (Child-handicap allowance). Verifies the registration gate, the under-18 age
/// gate, and that the benefit amount is keyed by disability degree.
/// </summary>
public class ChildHandicapAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Child-Handicap ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "CHILD_HANDICAP",
      "eligibility": [
        { "rule": "fact-equals", "fact": "childDisabilityRegistered", "value": true,
          "failCode": "CHILD_HANDICAP_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 18,
          "failCode": "CHILD_HANDICAP_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "childDisabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      2000.00,
          "accentuated": 1500.00,
          "medium":      1000.00
        }
      },
      "successCode": "CHILD_HANDICAP_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool registered, string degree, int childAgeYears)
        => new(new Dictionary<string, object?>
        {
            ["childDisabilityRegistered"] = registered,
            ["childDisabilityDegree"] = degree,
            ["childAgeYears"] = childAgeYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(registered: true, degree: "severe", childAgeYears: 8));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_HANDICAP_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(registered: false, degree: "severe", childAgeYears: 8));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_HANDICAP_INELIGIBLE_NOT_REGISTERED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(registered: true, degree: "severe", childAgeYears: 20));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_HANDICAP_INELIGIBLE_AGE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(registered: false, degree: "severe", childAgeYears: 20));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHILD_HANDICAP_INELIGIBLE_NOT_REGISTERED");
        result.Value.ReasonCodes.Should().Contain("CHILD_HANDICAP_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().NotContain("CHILD_HANDICAP_ELIGIBLE");
    }
}
