using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.7-D — Pensie pentru diplomați (Diplomat pension).
/// Verifies the past-diplomatic-status gate, the 15-year service-stage threshold,
/// and that the benefit is 75% of the last diplomat salary.
/// </summary>
public class DiplomatPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Diplomat-Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "DIPLOMAT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasDiplomat", "value": true,
          "failCode": "DIPLOMAT_PENSION_INELIGIBLE_NOT_DIPLOMAT" },
        { "rule": "fact-greater-than", "fact": "diplomaticServiceYears", "value": 14,
          "failCode": "DIPLOMAT_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastDiplomatSalaryMdl"
      },
      "successCode": "DIPLOMAT_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasDiplomat, int serviceYears, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["wasDiplomat"] = wasDiplomat,
            ["diplomaticServiceYears"] = serviceYears,
            ["lastDiplomatSalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasDiplomat: true, serviceYears: 20, lastSalary: Money.Mdl(12000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DIPLOMAT_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(9000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasDiplomat: false, serviceYears: 20, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DIPLOMAT_PENSION_INELIGIBLE_NOT_DIPLOMAT");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasDiplomat: true, serviceYears: 5, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DIPLOMAT_PENSION_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasDiplomat: false, serviceYears: 5, lastSalary: Money.Mdl(12000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DIPLOMAT_PENSION_INELIGIBLE_NOT_DIPLOMAT");
        result.Value.ReasonCodes.Should().Contain("DIPLOMAT_PENSION_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("DIPLOMAT_PENSION_ELIGIBLE");
    }
}
