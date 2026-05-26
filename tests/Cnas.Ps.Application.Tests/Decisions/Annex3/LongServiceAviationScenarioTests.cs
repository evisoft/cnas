using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.10-F — Pensie pentru vechime în muncă (aviație).
/// Verifies the aviation-crew gate, the 6 000-flight-hour threshold, and that
/// the benefit is 80% of the last salary.
/// </summary>
public class LongServiceAviationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "LONG_SERVICE_AVI",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasAviationCrew", "value": true,
          "failCode": "LONG_SERVICE_AVI_INELIGIBLE_NOT_CREW" },
        { "rule": "fact-greater-than", "fact": "flightHours", "value": 5999,
          "failCode": "LONG_SERVICE_AVI_INELIGIBLE_FLIGHT_HOURS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastAviationSalaryMdl"
      },
      "successCode": "LONG_SERVICE_AVI_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasCrew, int flightHours, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["wasAviationCrew"] = wasCrew,
            ["flightHours"] = flightHours,
            ["lastAviationSalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasCrew: true, flightHours: 8000, salary: Money.Mdl(15000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AVI_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(12000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasCrew: false, flightHours: 8000, salary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AVI_INELIGIBLE_NOT_CREW");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasCrew: true, flightHours: 1000, salary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("LONG_SERVICE_AVI_INELIGIBLE_FLIGHT_HOURS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasCrew: false, flightHours: 1000, salary: Money.Mdl(15000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_AVI_INELIGIBLE_NOT_CREW");
        result.Value.ReasonCodes.Should().Contain("LONG_SERVICE_AVI_INELIGIBLE_FLIGHT_HOURS");
        result.Value.ReasonCodes.Should().NotContain("LONG_SERVICE_AVI_ELIGIBLE");
    }
}
