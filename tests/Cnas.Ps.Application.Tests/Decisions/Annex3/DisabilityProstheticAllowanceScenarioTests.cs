using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.3-F — Compensare a costului protezelor
/// (Disability prosthetic allowance). Verifies the prescription gate, the
/// insurance gate, and that the benefit is 100% of the documented prosthetic cost.
/// </summary>
public class DisabilityProstheticAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Disability-Prosthetic ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "DISABILITY_PROSTHETIC",
      "eligibility": [
        { "rule": "fact-equals", "fact": "prostheticPrescribed", "value": true,
          "failCode": "DISABILITY_PROSTHETIC_INELIGIBLE_NO_PRESCRIPTION" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_PROSTHETIC_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "prostheticCostMdl"
      },
      "successCode": "DISABILITY_PROSTHETIC_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool prostheticPrescribed, bool isInsured, Money prostheticCost)
        => new(new Dictionary<string, object?>
        {
            ["prostheticPrescribed"] = prostheticPrescribed,
            ["isInsured"] = isInsured,
            ["prostheticCostMdl"] = prostheticCost,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(prostheticPrescribed: true, isInsured: true, prostheticCost: Money.Mdl(8500m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PROSTHETIC_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(8500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(prostheticPrescribed: false, isInsured: true, prostheticCost: Money.Mdl(8500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PROSTHETIC_INELIGIBLE_NO_PRESCRIPTION");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(prostheticPrescribed: true, isInsured: false, prostheticCost: Money.Mdl(8500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DISABILITY_PROSTHETIC_INELIGIBLE_NOT_INSURED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(prostheticPrescribed: false, isInsured: false, prostheticCost: Money.Mdl(8500m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DISABILITY_PROSTHETIC_INELIGIBLE_NO_PRESCRIPTION");
        result.Value.ReasonCodes.Should().Contain("DISABILITY_PROSTHETIC_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().NotContain("DISABILITY_PROSTHETIC_ELIGIBLE");
    }
}
