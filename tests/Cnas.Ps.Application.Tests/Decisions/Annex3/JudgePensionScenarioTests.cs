using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.7-B — Pensie pentru judecători (Judge pension).
/// Verifies the past-judicial-status gate, the 20-year service-stage threshold,
/// and that the benefit is 80% of the last judge salary.
/// </summary>
public class JudgePensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Judge-Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "JUDGE_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasJudge", "value": true,
          "failCode": "JUDGE_PENSION_INELIGIBLE_NOT_JUDGE" },
        { "rule": "fact-greater-than", "fact": "judicialServiceYears", "value": 19,
          "failCode": "JUDGE_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastJudgeSalaryMdl"
      },
      "successCode": "JUDGE_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasJudge, int serviceYears, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["wasJudge"] = wasJudge,
            ["judicialServiceYears"] = serviceYears,
            ["lastJudgeSalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasJudge: true, serviceYears: 25, lastSalary: Money.Mdl(15000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("JUDGE_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(12000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasJudge: false, serviceYears: 25, lastSalary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("JUDGE_PENSION_INELIGIBLE_NOT_JUDGE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasJudge: true, serviceYears: 10, lastSalary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("JUDGE_PENSION_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasJudge: false, serviceYears: 10, lastSalary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("JUDGE_PENSION_INELIGIBLE_NOT_JUDGE");
        result.Value.ReasonCodes.Should().Contain("JUDGE_PENSION_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("JUDGE_PENSION_ELIGIBLE");
    }
}
