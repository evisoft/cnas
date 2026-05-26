using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-C — Pensie parțială pentru limită de vârstă
/// (Partial old-age pension). Verifies the standard retirement age (63+), the
/// 15..33 inclusive contribution band — split into two distinct fail codes for the
/// lower and upper boundaries — and that the benefit is 25% of the average insured
/// income.
/// </summary>
public class PartialOldAgePensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Partial-Old-Age-Pension ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row by the
    /// infrastructure layer.
    /// </summary>
    private const string PartialOldAgeJson = """
    {
      "code": "PARTIAL_OLD_AGE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 63, "max": 120,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 14,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_LOW" },
        { "rule": "fact-less-than", "fact": "contributionYears", "value": 34,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_FULL" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 25,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "PARTIAL_OLD_AGE_ELIGIBLE"
    }
    """;

    private static readonly DateTime ClaimDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DobAt65 = ClaimDate.AddYears(-65);
    private static readonly DateTime DobAt60 = ClaimDate.AddYears(-60);

    private static DecisionFacts Facts(DateTime dobUtc, int contributionYears, Money averageIncome)
        => new(new Dictionary<string, object?>
        {
            ["dobUtc"] = dobUtc,
            ["claimDateUtc"] = ClaimDate,
            ["contributionYears"] = contributionYears,
            ["averageInsuredIncomeMdl"] = averageIncome,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            PartialOldAgeJson,
            Facts(dobUtc: DobAt65, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_OLD_AGE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2500m)); // 25% of 10000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            PartialOldAgeJson,
            Facts(dobUtc: DobAt60, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_OLD_AGE_INELIGIBLE_AGE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode_TooFewContributions()
    {
        var result = Engine.Evaluate(
            PartialOldAgeJson,
            Facts(dobUtc: DobAt65, contributionYears: 10, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_LOW");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode_QualifiesForFullPension()
    {
        var result = Engine.Evaluate(
            PartialOldAgeJson,
            Facts(dobUtc: DobAt65, contributionYears: 40, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_FULL");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            PartialOldAgeJson,
            Facts(dobUtc: DobAt60, contributionYears: 10, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PARTIAL_OLD_AGE_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().Contain("PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_LOW");
        result.Value.ReasonCodes.Should().NotContain("PARTIAL_OLD_AGE_ELIGIBLE");
    }
}
