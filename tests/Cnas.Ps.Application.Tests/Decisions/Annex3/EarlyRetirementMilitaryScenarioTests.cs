using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-C — Pensie anticipată pentru militari (Early-
/// retirement pension for military personnel). Verifies the military-status gate,
/// the 20-year service-stage threshold, and that the benefit is 50% of the last
/// military salary.
/// </summary>
public class EarlyRetirementMilitaryScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Early-Retirement-Military ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "EARLY_RETIREMENT_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "EARLY_RETIREMENT_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-greater-than", "fact": "serviceYears", "value": 19,
          "failCode": "EARLY_RETIREMENT_MIL_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "EARLY_RETIREMENT_MIL_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isMilitary, int serviceYears, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["isMilitaryPersonnel"] = isMilitary,
            ["serviceYears"] = serviceYears,
            ["lastMilitarySalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: true, serviceYears: 25, lastSalary: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("EARLY_RETIREMENT_MIL_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(5000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: false, serviceYears: 25, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("EARLY_RETIREMENT_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: true, serviceYears: 10, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("EARLY_RETIREMENT_MIL_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: false, serviceYears: 10, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("EARLY_RETIREMENT_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.ReasonCodes.Should().Contain("EARLY_RETIREMENT_MIL_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("EARLY_RETIREMENT_MIL_ELIGIBLE");
    }
}
