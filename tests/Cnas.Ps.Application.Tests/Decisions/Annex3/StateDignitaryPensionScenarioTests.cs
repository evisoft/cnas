using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.7-A — Pensie pentru demnitari de stat (State-
/// dignitary pension). Verifies the dignitary-status gate, the 5-year service-
/// term threshold, and that the benefit is a flat 8 000 MDL.
/// </summary>
public class StateDignitaryPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical State-Dignitary ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "STATE_DIGNITARY_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasStateDignitary", "value": true,
          "failCode": "STATE_DIGNITARY_PENSION_INELIGIBLE_NOT_DIGNITARY" },
        { "rule": "fact-greater-than", "fact": "serviceTermYears", "value": 4,
          "failCode": "STATE_DIGNITARY_PENSION_INELIGIBLE_SERVICE_TERM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 8000.00,
        "currency": "MDL"
      },
      "successCode": "STATE_DIGNITARY_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasDignitary, int serviceTermYears)
        => new(new Dictionary<string, object?>
        {
            ["wasStateDignitary"] = wasDignitary,
            ["serviceTermYears"] = serviceTermYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasDignitary: true, serviceTermYears: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("STATE_DIGNITARY_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(8000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasDignitary: false, serviceTermYears: 10));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("STATE_DIGNITARY_PENSION_INELIGIBLE_NOT_DIGNITARY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasDignitary: true, serviceTermYears: 2));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("STATE_DIGNITARY_PENSION_INELIGIBLE_SERVICE_TERM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasDignitary: false, serviceTermYears: 2));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("STATE_DIGNITARY_PENSION_INELIGIBLE_NOT_DIGNITARY");
        result.Value.ReasonCodes.Should().Contain("STATE_DIGNITARY_PENSION_INELIGIBLE_SERVICE_TERM");
        result.Value.ReasonCodes.Should().NotContain("STATE_DIGNITARY_PENSION_ELIGIBLE");
    }
}
