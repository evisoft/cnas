using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-C — Pensie pentru vechime în muncă
/// (agricultură). Verifies the agricultural-worker gate, the 30-year service
/// threshold, and that the benefit is 60% of the last agricultural salary.
/// </summary>
public class LongServiceAgricultureScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_AGRI",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasAgriculturalWorker", "value": true,
          "failCode": "LONG_SERVICE_AGRI_INELIGIBLE_NOT_AGRI" },
        { "rule": "fact-greater-than", "fact": "agriculturalServiceYears", "value": 29,
          "failCode": "LONG_SERVICE_AGRI_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 60,
        "referenceFact": "lastAgriSalaryMdl"
      },
      "successCode": "LONG_SERVICE_AGRI_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasAgri, int years, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasAgriculturalWorker"] = wasAgri,
            ["agriculturalServiceYears"] = years,
            ["lastAgriSalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasAgri: true, years: 31, salary: Money.Mdl(5000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AGRI_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasAgri: false, years: 31, salary: Money.Mdl(5000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AGRI_INELIGIBLE_NOT_AGRI");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasAgri: true, years: 15, salary: Money.Mdl(5000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AGRI_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasAgri: false, years: 15, salary: Money.Mdl(5000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_AGRI_INELIGIBLE_NOT_AGRI");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_AGRI_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_AGRI_ELIGIBLE");
    }
}
