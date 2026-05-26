using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-F — Pensie de orfan (Orphan pension). Verifies
/// the child-relationship gate, the both-parents-deceased gate, the under-18 age
/// gate, and that the benefit is 100% of the deceased's average insured income.
/// Three eligibility rules → a fifth scenario is added exercising the tertiary
/// refusal path.
/// </summary>
public class OrphanPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Orphan-Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "ORPHAN_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "child",
          "failCode": "ORPHAN_PENSION_INELIGIBLE_RELATIONSHIP" },
        { "rule": "fact-equals", "fact": "bothParentsDeceased", "value": true,
          "failCode": "ORPHAN_PENSION_INELIGIBLE_NOT_FULL_ORPHAN" },
        { "rule": "fact-less-than", "fact": "survivorAgeYears", "value": 18,
          "failCode": "ORPHAN_PENSION_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "ORPHAN_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(
        string relationship,
        bool bothParentsDeceased,
        int survivorAgeYears,
        Money averageIncome)
        => new(new Dictionary<string, object?>
        {
            ["relationshipToDeceased"] = relationship,
            ["bothParentsDeceased"] = bothParentsDeceased,
            ["survivorAgeYears"] = survivorAgeYears,
            ["deceasedAverageInsuredIncomeMdl"] = averageIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", bothParentsDeceased: true, survivorAgeYears: 10, averageIncome: Money.Mdl(6000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ORPHAN_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6000m)); // 100% of 6000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "spouse", bothParentsDeceased: true, survivorAgeYears: 10, averageIncome: Money.Mdl(6000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ORPHAN_PENSION_INELIGIBLE_RELATIONSHIP");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", bothParentsDeceased: false, survivorAgeYears: 10, averageIncome: Money.Mdl(6000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ORPHAN_PENSION_INELIGIBLE_NOT_FULL_ORPHAN");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_Age()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", bothParentsDeceased: true, survivorAgeYears: 19, averageIncome: Money.Mdl(6000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ORPHAN_PENSION_INELIGIBLE_AGE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "spouse", bothParentsDeceased: false, survivorAgeYears: 25, averageIncome: Money.Mdl(6000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("ORPHAN_PENSION_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().Contain("ORPHAN_PENSION_INELIGIBLE_NOT_FULL_ORPHAN");
        result.Value.ReasonCodes.Should().Contain("ORPHAN_PENSION_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().NotContain("ORPHAN_PENSION_ELIGIBLE");
    }
}
