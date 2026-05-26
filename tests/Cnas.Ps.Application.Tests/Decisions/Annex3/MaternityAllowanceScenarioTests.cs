using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.1-F — Indemnizație unică la naștere (mama)
/// (One-off maternity allowance, mother). Verifies the insured-mother gate, the
/// 1-year claim window, and that the benefit is a fixed 11 000 MDL.
/// </summary>
public class MaternityAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Maternity-Allowance ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// </summary>
    private const string MaternityAllowanceJson = """
    {
      "code": "MATERNITY_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "MATERNITY_ALLOWANCE_INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "MATERNITY_ALLOWANCE_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 11000.00,
        "currency": "MDL"
      },
      "successCode": "MATERNITY_ALLOWANCE_ELIGIBLE"
    }
    """;

    private static readonly DateTime Birth = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = Birth.AddDays(60);
    private static readonly DateTime LateClaim = Birth.AddDays(400);

    private static DecisionFacts Facts(bool isInsured, DateTime claimDateUtc)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["birthDateUtc"] = Birth,
            ["claimDateUtc"] = claimDateUtc,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            MaternityAllowanceJson,
            Facts(isInsured: true, claimDateUtc: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNITY_ALLOWANCE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(11000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            MaternityAllowanceJson,
            Facts(isInsured: false, claimDateUtc: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNITY_ALLOWANCE_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            MaternityAllowanceJson,
            Facts(isInsured: true, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNITY_ALLOWANCE_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            MaternityAllowanceJson,
            Facts(isInsured: false, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("MATERNITY_ALLOWANCE_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("MATERNITY_ALLOWANCE_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("MATERNITY_ALLOWANCE_ELIGIBLE");
    }
}
