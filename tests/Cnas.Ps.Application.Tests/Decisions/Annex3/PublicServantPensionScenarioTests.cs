using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.7-E — Pensie pentru funcționari publici (Public-
/// servant pension). Verifies the past-civil-servant-status gate, the 25-year
/// service-stage threshold, and that the benefit is 65% of the last public salary.
/// </summary>
public class PublicServantPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Public-Servant-Pension ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "PUBLIC_SERVANT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasCivilServant", "value": true,
          "failCode": "PUBLIC_SERVANT_PENSION_INELIGIBLE_NOT_SERVANT" },
        { "rule": "fact-greater-than", "fact": "publicServiceYears", "value": 24,
          "failCode": "PUBLIC_SERVANT_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 65,
        "referenceFact": "lastPublicSalaryMdl"
      },
      "successCode": "PUBLIC_SERVANT_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasCivilServant, int serviceYears, Money lastSalary)
        => new(new Dictionary<string, object?>
        {
            ["wasCivilServant"] = wasCivilServant,
            ["publicServiceYears"] = serviceYears,
            ["lastPublicSalaryMdl"] = lastSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasCivilServant: true, serviceYears: 30, lastSalary: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PUBLIC_SERVANT_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasCivilServant: false, serviceYears: 30, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PUBLIC_SERVANT_PENSION_INELIGIBLE_NOT_SERVANT");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasCivilServant: true, serviceYears: 10, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PUBLIC_SERVANT_PENSION_INELIGIBLE_SERVICE_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(wasCivilServant: false, serviceYears: 10, lastSalary: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PUBLIC_SERVANT_PENSION_INELIGIBLE_NOT_SERVANT");
        result.Value.ReasonCodes.Should().Contain("PUBLIC_SERVANT_PENSION_INELIGIBLE_SERVICE_YEARS");
        result.Value.ReasonCodes.Should().NotContain("PUBLIC_SERVANT_PENSION_ELIGIBLE");
    }
}
