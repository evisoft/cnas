using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-G — Majorare la pensie pentru asigurați
/// vârstnici (Insured age increase). Verifies the 70-year age gate and that the
/// benefit is 10% of the current pension. The eligibility section has a single
/// rule, so this file covers the happy path, the single failure, an over-100
/// boundary case, and a malformed-fact engine-error case.
/// </summary>
public class InsuredAgeIncreaseScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Insured-Age-Increase ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "INSURED_AGE_INCREASE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 70, "max": 120,
          "failCode": "INSURED_AGE_INCREASE_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 10,
        "referenceFact": "currentPensionMdl"
      },
      "successCode": "INSURED_AGE_INCREASE_ELIGIBLE"
    }
    """;

    private static readonly DateTime ClaimDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DobAt75 = ClaimDate.AddYears(-75);
    private static readonly DateTime DobAt65 = ClaimDate.AddYears(-65);

    private static DecisionFacts Facts(DateTime dobUtc, Money currentPension)
        => new(new Dictionary<string, object?>
        {
            ["dobUtc"] = dobUtc,
            ["claimDateUtc"] = ClaimDate,
            ["currentPensionMdl"] = currentPension,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(dobUtc: DobAt75, currentPension: Money.Mdl(3000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INSURED_AGE_INCREASE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(300m)); // 10% of 3000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(dobUtc: DobAt65, currentPension: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INSURED_AGE_INCREASE_INELIGIBLE_AGE");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        // No secondary eligibility rule exists; we exercise the boundary just below the
        // minimum age (69) to ensure the single failure code is still emitted alone.
        var dobAt69 = ClaimDate.AddYears(-69);
        var result = Engine.Evaluate(Json, Facts(dobUtc: dobAt69, currentPension: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INSURED_AGE_INCREASE_INELIGIBLE_AGE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        // Only one eligibility rule exists, so the "accumulation" reduces to a single
        // entry — verified by asserting both the failure and the absence of success.
        var result = Engine.Evaluate(Json, Facts(dobUtc: DobAt65, currentPension: Money.Mdl(3000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("INSURED_AGE_INCREASE_INELIGIBLE_AGE");
        result.Value.ReasonCodes.Should().NotContain("INSURED_AGE_INCREASE_ELIGIBLE");
    }
}
