using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.4-B — Bilet de reabilitare balneară (Spa
/// rehabilitation voucher). Verifies the recommendation gate and the
/// minimum-12-month interval gate. The "amount" is a 0 MDL sentinel because the
/// entitlement is a voucher; eligible decisions still surface 0 MDL so the
/// downstream workflow can detect approval.
/// </summary>
public class SpaRehabilitationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Spa-Rehab ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "SPA_REHAB",
      "eligibility": [
        { "rule": "fact-equals", "fact": "medicalRecommendationOnFile", "value": true,
          "failCode": "SPA_REHAB_INELIGIBLE_NO_RECOMMENDATION" },
        { "rule": "fact-greater-than", "fact": "lastRehabilitationYears", "value": 1,
          "failCode": "SPA_REHAB_INELIGIBLE_TOO_RECENT" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "SPA_REHAB_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool medicalRecommendationOnFile, decimal lastRehabilitationYears)
        => new(new Dictionary<string, object?>
        {
            ["medicalRecommendationOnFile"] = medicalRecommendationOnFile,
            ["lastRehabilitationYears"] = lastRehabilitationYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(medicalRecommendationOnFile: true, lastRehabilitationYears: 3m));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SPA_REHAB_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(0m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(medicalRecommendationOnFile: false, lastRehabilitationYears: 3m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SPA_REHAB_INELIGIBLE_NO_RECOMMENDATION");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(medicalRecommendationOnFile: true, lastRehabilitationYears: 0.5m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("SPA_REHAB_INELIGIBLE_TOO_RECENT");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(medicalRecommendationOnFile: false, lastRehabilitationYears: 0.5m));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("SPA_REHAB_INELIGIBLE_NO_RECOMMENDATION");
        result.Value.ReasonCodes.Should().Contain("SPA_REHAB_INELIGIBLE_TOO_RECENT");
        result.Value.ReasonCodes.Should().NotContain("SPA_REHAB_ELIGIBLE");
    }
}
