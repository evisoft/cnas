using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-F — Compensație pentru încălzire (Heating
/// allowance). Verifies the vulnerable-household certification gate, the 180-day
/// claim window relative to the heating-season start, and that the benefit is a
/// flat 1 200 MDL.
/// </summary>
public class HeatingAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Heating-Allowance ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "HEATING_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "HEATING_ALLOWANCE_INELIGIBLE_NOT_VULNERABLE" },
        { "rule": "date-within-days", "fact": "heatingSeasonStartUtc",
          "referenceFact": "claimDateUtc", "maxDays": 180,
          "failCode": "HEATING_ALLOWANCE_INELIGIBLE_OUTSIDE_WINDOW" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1200.00,
        "currency": "MDL"
      },
      "successCode": "HEATING_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static readonly DateTime SeasonStart = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = SeasonStart.AddDays(30);
    private static readonly DateTime LateClaim = SeasonStart.AddDays(220);

    private static DecisionFacts Facts(bool vulnerable, DateTime claimDate)
        => new(new Dictionary<string, object?>
        {
            ["householdCertifiedVulnerable"] = vulnerable,
            ["heatingSeasonStartUtc"] = SeasonStart,
            ["claimDateUtc"] = claimDate,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(vulnerable: true, claimDate: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("HEATING_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1200m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(vulnerable: false, claimDate: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("HEATING_ALLOWANCE_INELIGIBLE_NOT_VULNERABLE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(vulnerable: true, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("HEATING_ALLOWANCE_INELIGIBLE_OUTSIDE_WINDOW");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(vulnerable: false, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("HEATING_ALLOWANCE_INELIGIBLE_NOT_VULNERABLE");
        result.Value.ReasonCodes.Should().Contain("HEATING_ALLOWANCE_INELIGIBLE_OUTSIDE_WINDOW");
        result.Value.ReasonCodes.Should().NotContain("HEATING_ALLOWANCE_ELIGIBLE");
    }
}
