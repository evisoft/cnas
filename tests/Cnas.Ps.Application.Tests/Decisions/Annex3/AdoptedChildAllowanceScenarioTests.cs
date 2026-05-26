using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.1-C — Indemnizație unică la adopția copilului
/// (One-off allowance on child adoption). The ruleset is exercised against an
/// in-time happy path, the two refusal codes, and an accumulation scenario.
/// </summary>
public class AdoptedChildAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Adopted-Child Allowance ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row by the
    /// infrastructure layer.
    /// </summary>
    private const string AdoptedChildJson = """
    {
      "code": "ADOPTED_CHILD_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "ADOPTED_CHILD_INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "adoptionDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "ADOPTED_CHILD_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 9000.00,
        "currency": "MDL"
      },
      "successCode": "ADOPTED_CHILD_ELIGIBLE"
    }
    """;

    private static readonly DateTime AdoptionDate = new(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = AdoptionDate.AddDays(60);
    private static readonly DateTime LateClaim = AdoptionDate.AddDays(400);

    private static DecisionFacts Facts(bool isInsured, DateTime claimDateUtc)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["adoptionDateUtc"] = AdoptionDate,
            ["claimDateUtc"] = claimDateUtc,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(AdoptedChildJson, Facts(isInsured: true, claimDateUtc: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADOPTED_CHILD_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(9000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(AdoptedChildJson, Facts(isInsured: false, claimDateUtc: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADOPTED_CHILD_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(AdoptedChildJson, Facts(isInsured: true, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ADOPTED_CHILD_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(AdoptedChildJson, Facts(isInsured: false, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("ADOPTED_CHILD_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("ADOPTED_CHILD_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("ADOPTED_CHILD_ELIGIBLE");
    }
}
