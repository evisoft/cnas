using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.9-B — Pensie pentru persoanele represate politic
/// (Political-repressed pension). Verifies the rehabilitated-status gate and that
/// the benefit is a flat 2 800 MDL.
/// </summary>
/// <remarks>
/// This service has a single eligibility gate; the four canonical scenarios are
/// adapted as: (1) happy path; (2) ineligible when the status flag is false;
/// (3) ineligible when the status flag is supplied as <c>false</c> via a second
/// caller; (4) confirmation that no spurious success code accompanies a failure
/// (i.e., the only reason code is the failure code).
/// </remarks>
public class PoliticalRepressedPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Political-Repressed ruleset, identical to the JSON written into
    /// the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "POLITICAL_REPRESSED",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isPoliticallyRepressedRehabilitated", "value": true,
          "failCode": "POLITICAL_REPRESSED_INELIGIBLE_NOT_REHABILITATED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2800.00,
        "currency": "MDL"
      },
      "successCode": "POLITICAL_REPRESSED_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool rehabilitated)
        => new(new Dictionary<string, object?>
        {
            ["isPoliticallyRepressedRehabilitated"] = rehabilitated,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(rehabilitated: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("POLITICAL_REPRESSED_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2800m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(rehabilitated: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("POLITICAL_REPRESSED_INELIGIBLE_NOT_REHABILITATED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        // With a single-gate spec, the secondary variant repeats the only failure path
        // through a separate caller signature, confirming repeatability of the failure.
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["isPoliticallyRepressedRehabilitated"] = false,
        });

        var result = Engine.Evaluate(Json, facts);

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("POLITICAL_REPRESSED_INELIGIBLE_NOT_REHABILITATED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        // Single-gate variant: confirm the failure code is present and the success
        // code is never spuriously emitted, mirroring the canonical assertion shape.
        var result = Engine.Evaluate(Json, Facts(rehabilitated: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("POLITICAL_REPRESSED_INELIGIBLE_NOT_REHABILITATED");
        result.Value.ReasonCodes.Should().NotContain("POLITICAL_REPRESSED_ELIGIBLE");
    }
}
