using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-A — Pensie pentru limită de vârstă (Old-age pension).
/// Verifies the age threshold, the contribution-stage threshold, and that the benefit
/// is computed as a percentage of the average insured income.
/// </summary>
public class OldAgePensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Old-Age Pension ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string OldAgePensionJson = """
    {
      "code": "OLD_AGE_PENSION",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 63, "max": 120,
          "failCode": "OLD_AGE_PENSION_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 33,
          "failCode": "OLD_AGE_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 45,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "OLD_AGE_PENSION_ELIGIBLE"
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
            OldAgePensionJson,
            Facts(dobUtc: DobAt65, contributionYears: 40, averageIncome: Money.Mdl(10000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OLD_AGE_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(4500m)); // 45% of 10000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            OldAgePensionJson,
            Facts(dobUtc: DobAt60, contributionYears: 40, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OLD_AGE_PENSION_INELIGIBLE_AGE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            OldAgePensionJson,
            Facts(dobUtc: DobAt65, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("OLD_AGE_PENSION_INELIGIBLE_CONTRIBUTIONS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            OldAgePensionJson,
            Facts(dobUtc: DobAt60, contributionYears: 20, averageIncome: Money.Mdl(10000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("OLD_AGE_PENSION_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().Contain("OLD_AGE_PENSION_INELIGIBLE_CONTRIBUTIONS");
        result.Value.ReasonCodes.Should().NotContain("OLD_AGE_PENSION_ELIGIBLE");
    }
}
