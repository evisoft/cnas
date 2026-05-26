using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-B — Pensie anticipată pentru limită de vârstă
/// (Anticipated old-age pension). Verifies the 58–62 age window, the &gt; 36-year
/// contribution requirement, and that the benefit is 40% of the average insured
/// income.
/// </summary>
public class AnticipatedOldAgePensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Anticipated-Old-Age-Pension ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure
    /// layer.
    /// </summary>
    private const string AnticipatedOldAgeJson = """
    {
      "code": "ANTICIPATED_OLD_AGE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 58, "max": 62,
          "failCode": "ANTICIPATED_OLD_AGE_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 36,
          "failCode": "ANTICIPATED_OLD_AGE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 40,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "ANTICIPATED_OLD_AGE_ELIGIBLE"
    }
    """;

    private static readonly DateTime ClaimDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DobAt60 = ClaimDate.AddYears(-60);
    private static readonly DateTime DobAt55 = ClaimDate.AddYears(-55);

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
            AnticipatedOldAgeJson,
            Facts(dobUtc: DobAt60, contributionYears: 40, averageIncome: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ANTICIPATED_OLD_AGE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4000m)); // 40% of 10000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            AnticipatedOldAgeJson,
            Facts(dobUtc: DobAt55, contributionYears: 40, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ANTICIPATED_OLD_AGE_INELIGIBLE_AGE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            AnticipatedOldAgeJson,
            Facts(dobUtc: DobAt60, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ANTICIPATED_OLD_AGE_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            AnticipatedOldAgeJson,
            Facts(dobUtc: DobAt55, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("ANTICIPATED_OLD_AGE_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().Contain("ANTICIPATED_OLD_AGE_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("ANTICIPATED_OLD_AGE_ELIGIBLE");
    }
}
