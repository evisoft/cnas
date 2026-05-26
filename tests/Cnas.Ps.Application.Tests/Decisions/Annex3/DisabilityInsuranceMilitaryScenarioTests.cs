using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.14-B — Asigurare dizabilitate militar. Verifies
/// the military-personnel gate, the disability-from-service gate, and that the
/// benefit is 100% of the last military salary.
/// </summary>
public class DisabilityInsuranceMilitaryScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "DISABILITY_INS_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "DISABILITY_INS_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-equals", "fact": "disabilityFromServiceVerified", "value": true,
          "failCode": "DISABILITY_INS_MIL_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "DISABILITY_INS_MIL_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isMilitary, bool verified, Money salary)
        => new(new Dictionary<string, object?>
        {
            ["isMilitaryPersonnel"] = isMilitary,
            ["disabilityFromServiceVerified"] = verified,
            ["lastMilitarySalaryMdl"] = salary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isMilitary: true, verified: true, salary: Money.Mdl(12000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_INS_MIL_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(12000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isMilitary: false, verified: true, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_INS_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isMilitary: true, verified: false, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_INS_MIL_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isMilitary: false, verified: false, salary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_INS_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_INS_MIL_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_INS_MIL_ELIGIBLE");
    }
}
