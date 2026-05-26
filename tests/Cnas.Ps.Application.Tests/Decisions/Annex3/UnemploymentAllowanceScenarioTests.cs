using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-A — Ajutor de șomaj (Unemployment allowance).
/// Verifies the SI-AISSS registration gate, the 9-month contribution threshold,
/// and that the benefit is 50% of the last average salary.
/// </summary>
public class UnemploymentAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Unemployment-Allowance ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "UNEMPLOYMENT_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "registeredAtSiAisss", "value": true,
          "failCode": "UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 8,
          "failCode": "UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "lastAverageSalaryMdl"
      },
      "successCode": "UNEMPLOYMENT_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool registered, int contributionMonths, Money lastAverageSalary)
        => new(new Dictionary<string, object?>
        {
            ["registeredAtSiAisss"] = registered,
            ["contributionMonths"] = contributionMonths,
            ["lastAverageSalaryMdl"] = lastAverageSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(registered: true, contributionMonths: 24, lastAverageSalary: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("UNEMPLOYMENT_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m)); // 50% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(registered: false, contributionMonths: 24, lastAverageSalary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_NOT_REGISTERED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(registered: true, contributionMonths: 3, lastAverageSalary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(registered: false, contributionMonths: 3, lastAverageSalary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_NOT_REGISTERED");
        result.Value.ReasonCodes.Should().Contain("UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("UNEMPLOYMENT_ALLOWANCE_ELIGIBLE");
    }
}
