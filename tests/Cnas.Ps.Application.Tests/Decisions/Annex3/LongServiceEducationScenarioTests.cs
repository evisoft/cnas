using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-A — Pensie pentru vechime în muncă (educație)
/// (Long-service pension for educators). Verifies the educator-status gate, the
/// 25-year service threshold, and that the benefit is 75% of the last salary.
/// </summary>
public class LongServiceEducationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_EDU",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasEducator", "value": true,
          "failCode": "LONG_SERVICE_EDU_INELIGIBLE_NOT_EDUCATOR" },
        { "rule": "fact-greater-than", "fact": "educationServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_EDU_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastEducatorSalaryMdl"
      },
      "successCode": "LONG_SERVICE_EDU_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasEducator, int years, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasEducator"] = wasEducator,
            ["educationServiceYears"] = years,
            ["lastEducatorSalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasEducator: true, years: 30, salary: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_EDU_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6000m)); // 75% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasEducator: false, years: 30, salary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_EDU_INELIGIBLE_NOT_EDUCATOR");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasEducator: true, years: 10, salary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_EDU_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasEducator: false, years: 10, salary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_EDU_INELIGIBLE_NOT_EDUCATOR");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_EDU_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_EDU_ELIGIBLE");
    }
}
