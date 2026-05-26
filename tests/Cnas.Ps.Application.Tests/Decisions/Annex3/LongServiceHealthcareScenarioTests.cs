using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-B — Pensie pentru vechime în muncă (sănătate)
/// (Long-service pension for healthcare workers). Verifies the healthcare-worker
/// gate, the 25-year service threshold, and that the benefit is 75% of the last
/// salary.
/// </summary>
public class LongServiceHealthcareScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_HEALTH",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasHealthcareWorker", "value": true,
          "failCode": "LONG_SERVICE_HEALTH_INELIGIBLE_NOT_HEALTHCARE" },
        { "rule": "fact-greater-than", "fact": "healthcareServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_HEALTH_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastHealthcareSalaryMdl"
      },
      "successCode": "LONG_SERVICE_HEALTH_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasHealthcareWorker, int years, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasHealthcareWorker"] = wasHealthcareWorker,
            ["healthcareServiceYears"] = years,
            ["lastHealthcareSalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasHealthcareWorker: true, years: 28, salary: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_HEALTH_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(7500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasHealthcareWorker: false, years: 28, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_HEALTH_INELIGIBLE_NOT_HEALTHCARE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasHealthcareWorker: true, years: 5, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_HEALTH_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasHealthcareWorker: false, years: 5, salary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_HEALTH_INELIGIBLE_NOT_HEALTHCARE");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_HEALTH_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_HEALTH_ELIGIBLE");
    }
}
