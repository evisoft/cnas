using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-D — Pensie de urmaș pentru copil în studii
/// (Survivor pension — child in education). Verifies the child-relationship gate,
/// the enrolment gate, the under-23 age gate, and that the benefit is 50% of the
/// deceased's average insured income. Three eligibility rules → a fifth scenario
/// is added exercising the tertiary refusal path.
/// </summary>
public class SurvivorChildEducationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Survivor-Child-Education ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "SURVIVOR_CHILD_EDUCATION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "child",
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_RELATIONSHIP" },
        { "rule": "fact-equals", "fact": "enrolledInEducation", "value": true,
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_NOT_ENROLLED" },
        { "rule": "fact-less-than", "fact": "survivorAgeYears", "value": 23,
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "SURVIVOR_CHILD_EDUCATION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(
        string relationship,
        bool enrolled,
        int survivorAgeYears,
        Money averageIncome)
        => new(new Dictionary<string, object?>
        {
            ["relationshipToDeceased"] = relationship,
            ["enrolledInEducation"] = enrolled,
            ["survivorAgeYears"] = survivorAgeYears,
            ["deceasedAverageInsuredIncomeMdl"] = averageIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", enrolled: true, survivorAgeYears: 20, averageIncome: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_CHILD_EDUCATION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m)); // 50% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "spouse", enrolled: true, survivorAgeYears: 20, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_RELATIONSHIP");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", enrolled: false, survivorAgeYears: 20, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_NOT_ENROLLED");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_Age()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "child", enrolled: true, survivorAgeYears: 25, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_AGE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            Json,
            Facts(relationship: "spouse", enrolled: false, survivorAgeYears: 25, averageIncome: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_RELATIONSHIP");
        result.Value.ReasonCodes.Should().Contain("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_NOT_ENROLLED");
        result.Value.ReasonCodes.Should().Contain("SURVIVOR_CHILD_EDUCATION_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().NotContain("SURVIVOR_CHILD_EDUCATION_ELIGIBLE");
    }
}
