using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-B — Ajutor de deces (Funeral allowance).
/// Verifies the deceased-insurance gate, the payer-relationship gate, the 1-year
/// claim window, and that the benefit is a fixed 1 500 MDL.
/// </summary>
public class FuneralAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Funeral-Allowance ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string FuneralAllowanceJson = """
    {
      "code": "FUNERAL_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedHadInsurance", "value": true,
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-in-set", "fact": "payerRelationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_RELATIONSHIP" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "FUNERAL_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static readonly DateTime DeathDate = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = DeathDate.AddDays(30);
    private static readonly DateTime LateClaim = DeathDate.AddDays(400);

    private static DecisionFacts Facts(
        bool deceasedHadInsurance,
        string payerRelationship,
        DateTime claimDateUtc)
        => new(new Dictionary<string, object?>
        {
            ["deceasedHadInsurance"] = deceasedHadInsurance,
            ["payerRelationshipToDeceased"] = payerRelationship,
            ["dateOfDeathUtc"] = DeathDate,
            ["claimDateUtc"] = claimDateUtc,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            FuneralAllowanceJson,
            Facts(deceasedHadInsurance: true, payerRelationship: "spouse", claimDateUtc: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode_NotInsured()
    {
        var result = Engine.Evaluate(
            FuneralAllowanceJson,
            Facts(deceasedHadInsurance: false, payerRelationship: "spouse", claimDateUtc: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_ALLOWANCE_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode_Relationship()
    {
        var result = Engine.Evaluate(
            FuneralAllowanceJson,
            Facts(deceasedHadInsurance: true, payerRelationship: "sibling", claimDateUtc: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_ALLOWANCE_INELIGIBLE_RELATIONSHIP");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_LateClaim()
    {
        var result = Engine.Evaluate(
            FuneralAllowanceJson,
            Facts(deceasedHadInsurance: true, payerRelationship: "spouse", claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_ALLOWANCE_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            FuneralAllowanceJson,
            Facts(deceasedHadInsurance: false, payerRelationship: "sibling", claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("FUNERAL_ALLOWANCE_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("FUNERAL_ALLOWANCE_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().Contain("FUNERAL_ALLOWANCE_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("FUNERAL_ALLOWANCE_ELIGIBLE");
    }
}
