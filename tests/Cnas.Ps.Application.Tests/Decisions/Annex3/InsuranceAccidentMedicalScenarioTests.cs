using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.18-A — Asigurare accident (rambursare medical).
/// Verifies the insured gate, the 30-day reporting gate, and that the benefit
/// is 100% of the medical costs incurred.
/// </summary>
public class InsuranceAccidentMedicalScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "INS_ACCIDENT_MED",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "INS_ACCIDENT_MED_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "accidentReportedWithin30Days", "value": true,
          "failCode": "INS_ACCIDENT_MED_INELIGIBLE_LATE_REPORT" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "medicalCostsMdl"
      },
      "successCode": "INS_ACCIDENT_MED_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isInsured, bool reportedOnTime, Money costs)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["accidentReportedWithin30Days"] = reportedOnTime,
            ["medicalCostsMdl"] = costs,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: true, reportedOnTime: true, costs: Money.Mdl(7500m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INS_ACCIDENT_MED_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(7500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: false, reportedOnTime: true, costs: Money.Mdl(7500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INS_ACCIDENT_MED_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: true, reportedOnTime: false, costs: Money.Mdl(7500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INS_ACCIDENT_MED_INELIGIBLE_LATE_REPORT");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: false, reportedOnTime: false, costs: Money.Mdl(7500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("INS_ACCIDENT_MED_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("INS_ACCIDENT_MED_INELIGIBLE_LATE_REPORT");
        result.Value.ReasonCodes.Should().NotContain("INS_ACCIDENT_MED_ELIGIBLE");
    }
}
