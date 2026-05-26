using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.7-C — Pensie pentru procurori (Prosecutor pension).
/// Verifies the past-prosecutor-status gate, the 20-year service-stage threshold,
/// and that the benefit is 80% of the last prosecutor salary.
/// </summary>
public class ProsecutorPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Prosecutor-Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "PROSECUTOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProsecutor", "value": true,
          "failCode": "PROSECUTOR_PENSION_INELIGIBLE_NOT_PROSECUTOR" },
        { "rule": "fact-greater-than", "fact": "prosecutorServiceYears", "value": 19,
          "failCode": "PROSECUTOR_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastProsecutorSalaryMdl"
      },
      "successCode": "PROSECUTOR_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasProsecutor, int serviceYears, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["wasProsecutor"] = wasProsecutor,
            ["prosecutorServiceYears"] = serviceYears,
            ["lastProsecutorSalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasProsecutor: true, serviceYears: 25, lastSalary: Money.Mdl(12000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROSECUTOR_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(9600m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasProsecutor: false, serviceYears: 25, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROSECUTOR_PENSION_INELIGIBLE_NOT_PROSECUTOR");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasProsecutor: true, serviceYears: 10, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROSECUTOR_PENSION_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasProsecutor: false, serviceYears: 10, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PROSECUTOR_PENSION_INELIGIBLE_NOT_PROSECUTOR");
        result.Value.ReasonCodes.Should().Contain("PROSECUTOR_PENSION_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("PROSECUTOR_PENSION_ELIGIBLE");
    }
}
