using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-D — Pensie pentru vechime în muncă (minerit)
/// (Long-service pension for miners). Verifies the miner-status gate, the
/// 20-year service threshold, and that the benefit is 70% of the last salary.
/// </summary>
public class LongServiceMiningScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_MINE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasMiner", "value": true,
          "failCode": "LONG_SERVICE_MINE_INELIGIBLE_NOT_MINER" },
        { "rule": "fact-greater-than", "fact": "miningServiceYears", "value": 19,
          "failCode": "LONG_SERVICE_MINE_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "lastMiningSalaryMdl"
      },
      "successCode": "LONG_SERVICE_MINE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasMiner, int years, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasMiner"] = wasMiner,
            ["miningServiceYears"] = years,
            ["lastMiningSalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasMiner: true, years: 22, salary: Money.Mdl(12000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_MINE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(8400m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasMiner: false, years: 22, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_MINE_INELIGIBLE_NOT_MINER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasMiner: true, years: 5, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_MINE_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasMiner: false, years: 5, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_MINE_INELIGIBLE_NOT_MINER");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_MINE_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_MINE_ELIGIBLE");
    }
}
