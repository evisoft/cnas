using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-D — Indemnizație pentru îngrijirea copilului
/// cu dizabilitate (Child-disability care allowance). Verifies the disability
/// gate, the under-18 age gate, and that the benefit amount is keyed by degree.
/// </summary>
public class ChildDisabilityCareScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Child-Disability-Care ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "CHILD_DISABILITY_CARE",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "childDisabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "CHILD_DISABILITY_CARE_INELIGIBLE_DEGREE" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 18,
          "failCode": "CHILD_DISABILITY_CARE_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "childDisabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      1800.00,
          "accentuated": 1400.00
        }
      },
      "successCode": "CHILD_DISABILITY_CARE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(string childDisabilityDegree, int childAgeYears)
        => new(new Dictionary<string, object?>
        {
            ["childDisabilityDegree"] = childDisabilityDegree,
            ["childAgeYears"] = childAgeYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(childDisabilityDegree: "severe", childAgeYears: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_DISABILITY_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1800m));
    }

    [Fact]
    public void Happy_Path_AccentuatedDegree_LooksUpLowerTier()
    {
        var result = Engine.Evaluate(Json, Facts(childDisabilityDegree: "accentuated", childAgeYears: 10));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(1400m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(childDisabilityDegree: "medium", childAgeYears: 10));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_DISABILITY_CARE_INELIGIBLE_DEGREE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(childDisabilityDegree: "severe", childAgeYears: 19));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_DISABILITY_CARE_INELIGIBLE_AGE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(childDisabilityDegree: "medium", childAgeYears: 25));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHILD_DISABILITY_CARE_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().Contain("CHILD_DISABILITY_CARE_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().NotContain("CHILD_DISABILITY_CARE_ELIGIBLE");
    }
}
