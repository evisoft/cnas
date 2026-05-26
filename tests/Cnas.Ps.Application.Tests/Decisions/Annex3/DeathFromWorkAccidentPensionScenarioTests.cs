using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-C — Pensie de urmaș (deces din accident de
/// muncă) (Survivor pension, death from work accident). Verifies the commission-
/// verification gate, the survivor-relationship gate, and that the benefit is
/// 100% of the deceased's average insured income.
/// </summary>
public class DeathFromWorkAccidentPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Death-from-Work-Accident-Pension ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row by the
    /// infrastructure layer.
    /// </summary>
    private const string DeathWorkAccidentJson = """
    {
      "code": "DEATH_WORK_ACCIDENT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "DEATH_WORK_ACCIDENT_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(
        bool accidentVerified,
        string relationshipToDeceased,
        Money deceasedAverageIncome)
        => new(new Dictionary<string, object?>
        {
            ["accidentVerifiedByCommission"] = accidentVerified,
            ["relationshipToDeceased"] = relationshipToDeceased,
            ["deceasedAverageInsuredIncomeMdl"] = deceasedAverageIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            DeathWorkAccidentJson,
            Facts(accidentVerified: true, relationshipToDeceased: "spouse",
                  deceasedAverageIncome: Money.Mdl(9000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEATH_WORK_ACCIDENT_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(9000m)); // 100% of 9000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            DeathWorkAccidentJson,
            Facts(accidentVerified: false, relationshipToDeceased: "spouse",
                  deceasedAverageIncome: Money.Mdl(9000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_NOT_VERIFIED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            DeathWorkAccidentJson,
            Facts(accidentVerified: true, relationshipToDeceased: "sibling",
                  deceasedAverageIncome: Money.Mdl(9000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_RELATIONSHIP");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            DeathWorkAccidentJson,
            Facts(accidentVerified: false, relationshipToDeceased: "sibling",
                  deceasedAverageIncome: Money.Mdl(9000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_NOT_VERIFIED");
        result.Value.ReasonCodes.Should().Contain("DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().NotContain("DEATH_WORK_ACCIDENT_PENSION_ELIGIBLE");
    }
}
