using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-D — Pensie de invaliditate parțială pentru
/// militari (Partial-disability pension for military personnel). Verifies the
/// military-status gate, the service-related-disability verification gate, and
/// that the benefit is 70% of the last military salary.
/// </summary>
public class PartialDisabilityMilitaryScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Partial-Disability-Military ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "PARTIAL_DISABILITY_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-equals", "fact": "disabilityFromServiceVerified", "value": true,
          "failCode": "PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "PARTIAL_DISABILITY_MIL_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isMilitary, bool verified, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["isMilitaryPersonnel"] = isMilitary,
            ["disabilityFromServiceVerified"] = verified,
            ["lastMilitarySalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: true, verified: true, lastSalary: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_DISABILITY_MIL_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(7000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: false, verified: true, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: true, verified: false, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_VERIFIED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(isMilitary: false, verified: false, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_MILITARY");
        result.Value.ReasonCodes.Should().Contain("PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().NotContain("PARTIAL_DISABILITY_MIL_ELIGIBLE");
    }
}
