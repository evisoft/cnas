using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.12-C — Alocație pentru părinte unic.
/// Verifies the single-parent gate, the at-least-one-dependent gate, and that
/// the benefit is a fixed 1 200 MDL.
/// </summary>
public class SingleParentAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "SINGLE_PARENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isSingleParent", "value": true,
          "failCode": "SINGLE_PARENT_INELIGIBLE_NOT_SINGLE_PARENT" },
        { "rule": "fact-greater-than", "fact": "dependentChildrenCount", "value": 0,
          "failCode": "SINGLE_PARENT_INELIGIBLE_NO_DEPENDENTS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1200.00,
        "currency": "MDL"
      },
      "successCode": "SINGLE_PARENT_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isSingle, int dependents)
        => new(new Dictionary<string, object?>
        {
            ["isSingleParent"] = isSingle,
            ["dependentChildrenCount"] = dependents,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isSingle: true, dependents: 2));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SINGLE_PARENT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1200m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isSingle: false, dependents: 2));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SINGLE_PARENT_INELIGIBLE_NOT_SINGLE_PARENT");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isSingle: true, dependents: 0));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SINGLE_PARENT_INELIGIBLE_NO_DEPENDENTS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isSingle: false, dependents: 0));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SINGLE_PARENT_INELIGIBLE_NOT_SINGLE_PARENT");
        result.Value.ReasonCodes.Should().Contain("SINGLE_PARENT_INELIGIBLE_NO_DEPENDENTS");
        result.Value.ReasonCodes.Should().NotContain("SINGLE_PARENT_ELIGIBLE");
    }
}
