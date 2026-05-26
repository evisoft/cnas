using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions;

/// <summary>
/// Acceptance tests for Annex 3.1-A — Indemnizație la nașterea copilului (Birth grant).
/// These tests load the JSON ruleset published by the engine in <see cref="BirthGrantRules"/>
/// and exercise the engine against representative real-world scenarios.
/// </summary>
public class BirthGrantScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Birth Grant ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// Kept inline so this test does not depend on Infrastructure.
    /// </summary>
    private const string BirthGrantJson = """
    {
      "code": "BIRTH_GRANT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "birthOrder",
        "currency": "MDL",
        "table": { "1": 11000.00, "2": 12000.00, "default": 13000.00 }
      },
      "successCode": "BIRTH_GRANT_ELIGIBLE"
    }
    """;

    private static readonly DateTime Birth = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = Birth.AddDays(30);
    private static readonly DateTime LateClaim = Birth.AddDays(400);

    private static DecisionFacts Facts(bool isInsured, int birthOrder, DateTime claimDateUtc)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["birthDateUtc"] = Birth,
            ["claimDateUtc"] = claimDateUtc,
            ["birthOrder"] = birthOrder,
        });

    [Fact]
    public void InsuredOnTime_FirstChild_EligibleAt11000Mdl()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(true, 1, OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("BIRTH_GRANT_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(11000m));
    }

    [Fact]
    public void InsuredOnTime_SecondChild_EligibleAt12000Mdl()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(true, 2, OnTimeClaim));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(12000m));
    }

    [Fact]
    public void InsuredOnTime_FifthChild_FallsBackToDefault13000Mdl()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(true, 5, OnTimeClaim));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(13000m));
    }

    [Fact]
    public void Uninsured_OnTime_Ineligible_NotInsured()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(false, 1, OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Insured_LateClaim_Ineligible_LateClaim()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(true, 1, LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Uninsured_AndLate_BothReasonCodesAccumulate()
    {
        var result = Engine.Evaluate(BirthGrantJson, Facts(false, 1, LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("BIRTH_GRANT_ELIGIBLE");
    }
}
