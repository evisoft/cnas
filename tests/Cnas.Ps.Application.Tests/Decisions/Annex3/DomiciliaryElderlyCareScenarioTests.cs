using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.13-B — Îngrijire la domiciliu (vârstnici).
/// Verifies the requires-care gate, the elderly-age threshold, and that the
/// benefit is 50% of the reference care cost.
/// </summary>
public class DomiciliaryElderlyCareScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "DOMICILIARY_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "requiresDomiciliaryCare", "value": true,
          "failCode": "DOMICILIARY_CARE_INELIGIBLE_NOT_REQUIRED" },
        { "rule": "fact-greater-than", "fact": "elderlyAgeYears", "value": 69,
          "failCode": "DOMICILIARY_CARE_INELIGIBLE_TOO_YOUNG" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "referenceCareCostMdl"
      },
      "successCode": "DOMICILIARY_CARE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool requiresCare, int age, Money cost)
        => new(new Dictionary<string, object?>
        {
            ["requiresDomiciliaryCare"] = requiresCare,
            ["elderlyAgeYears"] = age,
            ["referenceCareCostMdl"] = cost,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(requiresCare: true, age: 75, cost: Money.Mdl(4000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DOMICILIARY_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(requiresCare: false, age: 75, cost: Money.Mdl(4000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DOMICILIARY_CARE_INELIGIBLE_NOT_REQUIRED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(requiresCare: true, age: 60, cost: Money.Mdl(4000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DOMICILIARY_CARE_INELIGIBLE_TOO_YOUNG");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(requiresCare: false, age: 60, cost: Money.Mdl(4000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DOMICILIARY_CARE_INELIGIBLE_NOT_REQUIRED");
        result.Value.ReasonCodes.Should().Contain("DOMICILIARY_CARE_INELIGIBLE_TOO_YOUNG");
        result.Value.ReasonCodes.Should().NotContain("DOMICILIARY_CARE_ELIGIBLE");
    }
}
