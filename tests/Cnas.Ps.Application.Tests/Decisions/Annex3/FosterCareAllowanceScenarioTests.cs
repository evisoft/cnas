using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-G — Indemnizație pentru asistență parentală
/// profesionistă (Foster-care allowance). Verifies the foster-family licensing
/// gate, the at-least-one-child gate, and that the benefit is 100% of the
/// reference foster-care monthly rate.
/// </summary>
public class FosterCareAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Foster-Care ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "FOSTER_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "licensedFosterFamily", "value": true,
          "failCode": "FOSTER_CARE_INELIGIBLE_NOT_LICENSED" },
        { "rule": "fact-greater-than", "fact": "childrenInCareCount", "value": 0,
          "failCode": "FOSTER_CARE_INELIGIBLE_NO_CHILDREN" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "referenceFosterRateMdl"
      },
      "successCode": "FOSTER_CARE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool licensed, int children, Money referenceRate)
        => new(new Dictionary<string, object?>
        {
            ["licensedFosterFamily"] = licensed,
            ["childrenInCareCount"] = children,
            ["referenceFosterRateMdl"] = referenceRate,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(licensed: true, children: 2, referenceRate: Money.Mdl(3000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FOSTER_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(licensed: false, children: 2, referenceRate: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FOSTER_CARE_INELIGIBLE_NOT_LICENSED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(licensed: true, children: 0, referenceRate: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FOSTER_CARE_INELIGIBLE_NO_CHILDREN");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(licensed: false, children: 0, referenceRate: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("FOSTER_CARE_INELIGIBLE_NOT_LICENSED");
        result.Value.ReasonCodes.Should().Contain("FOSTER_CARE_INELIGIBLE_NO_CHILDREN");
        result.Value.ReasonCodes.Should().NotContain("FOSTER_CARE_ELIGIBLE");
    }
}
