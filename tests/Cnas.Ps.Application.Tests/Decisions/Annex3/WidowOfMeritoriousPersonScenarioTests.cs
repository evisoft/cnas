using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-E — Indemnizație pentru văduva persoanei cu
/// merite deosebite (Widow of meritorious person allowance). Verifies the
/// meritorious-status gate, the spouse-relationship gate, and that the benefit
/// is a fixed 2 500 MDL.
/// </summary>
public class WidowOfMeritoriousPersonScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Widow-of-Meritorious-Person ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "WIDOW_MERITORIOUS",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedWasMeritorious", "value": true,
          "failCode": "WIDOW_MERITORIOUS_INELIGIBLE_NOT_MERITORIOUS" },
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "spouse",
          "failCode": "WIDOW_MERITORIOUS_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "WIDOW_MERITORIOUS_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool deceasedWasMeritorious, string relationship)
        => new(new Dictionary<string, object?>
        {
            ["deceasedWasMeritorious"] = deceasedWasMeritorious,
            ["relationshipToDeceased"] = relationship,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasMeritorious: true, relationship: "spouse"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WIDOW_MERITORIOUS_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasMeritorious: false, relationship: "spouse"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WIDOW_MERITORIOUS_INELIGIBLE_NOT_MERITORIOUS");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasMeritorious: true, relationship: "child"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WIDOW_MERITORIOUS_INELIGIBLE_RELATIONSHIP");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasMeritorious: false, relationship: "child"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WIDOW_MERITORIOUS_INELIGIBLE_NOT_MERITORIOUS");
        result.Value.ReasonCodes.Should().Contain("WIDOW_MERITORIOUS_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().NotContain("WIDOW_MERITORIOUS_ELIGIBLE");
    }
}
