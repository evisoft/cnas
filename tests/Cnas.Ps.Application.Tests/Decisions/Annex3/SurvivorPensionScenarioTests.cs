using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-A — Pensie de urmaș (Survivor pension).
/// Verifies that the survivor must be in a recognized relationship to a
/// previously-insured deceased and that the benefit is 75% of the deceased's
/// average insured income.
/// </summary>
/// <remarks>
/// The child-age cap (≤ 18 years) documented on
/// <c>SurvivorPensionPassport</c> is enforced by the calling application
/// layer, not by the engine — these tests therefore only exercise the two
/// flat rules embedded in the passport JSON.
/// </remarks>
public class SurvivorPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Survivor Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string SurvivorPensionJson = """
    {
      "code": "SURVIVOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedHadInsurance", "value": true,
          "failCode": "SURVIVOR_PENSION_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "SURVIVOR_PENSION_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "SURVIVOR_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(
        bool deceasedHadInsurance,
        string relationshipToDeceased,
        int survivorAgeYears,
        Money deceasedAverageInsuredIncome)
        => new(new Dictionary<string, object?>
        {
            ["deceasedHadInsurance"] = deceasedHadInsurance,
            ["relationshipToDeceased"] = relationshipToDeceased,
            ["survivorAgeYears"] = survivorAgeYears,
            ["deceasedAverageInsuredIncomeMdl"] = deceasedAverageInsuredIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            SurvivorPensionJson,
            Facts(
                deceasedHadInsurance: true,
                relationshipToDeceased: "spouse",
                survivorAgeYears: 55,
                deceasedAverageInsuredIncome: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(6000m)); // 75% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            SurvivorPensionJson,
            Facts(
                deceasedHadInsurance: false,
                relationshipToDeceased: "spouse",
                survivorAgeYears: 55,
                deceasedAverageInsuredIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_PENSION_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            SurvivorPensionJson,
            Facts(
                deceasedHadInsurance: true,
                relationshipToDeceased: "sibling",
                survivorAgeYears: 30,
                deceasedAverageInsuredIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_PENSION_INELIGIBLE_RELATIONSHIP");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            SurvivorPensionJson,
            Facts(
                deceasedHadInsurance: false,
                relationshipToDeceased: "sibling",
                survivorAgeYears: 30,
                deceasedAverageInsuredIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SURVIVOR_PENSION_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("SURVIVOR_PENSION_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().NotContain("SURVIVOR_PENSION_ELIGIBLE");
    }
}
