using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-E — Pensie pentru vechime în muncă (transport
/// feroviar). Verifies the railway-worker gate, the 25-year service threshold,
/// and that the benefit is 65% of the last salary.
/// </summary>
public class LongServiceRailwayScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_RAIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasRailwayWorker", "value": true,
          "failCode": "LONG_SERVICE_RAIL_INELIGIBLE_NOT_RAILWAY" },
        { "rule": "fact-greater-than", "fact": "railwayServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_RAIL_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 65,
        "referenceFact": "lastRailwaySalaryMdl"
      },
      "successCode": "LONG_SERVICE_RAIL_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasRail, int years, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasRailwayWorker"] = wasRail,
            ["railwayServiceYears"] = years,
            ["lastRailwaySalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasRail: true, years: 30, salary: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_RAIL_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasRail: false, years: 30, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_RAIL_INELIGIBLE_NOT_RAILWAY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasRail: true, years: 10, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_RAIL_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasRail: false, years: 10, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_RAIL_INELIGIBLE_NOT_RAILWAY");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_RAIL_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_RAIL_ELIGIBLE");
    }
}
