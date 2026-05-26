using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.6-B — Indemnizație pentru reorientare
/// profesională (Professional reorientation allowance). Verifies the SI-AISSS
/// registration gate, the commission-approval gate, and that the benefit is a
/// fixed 1 500 MDL.
/// </summary>
public class ProfessionalReorientationScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Professional-Reorientation ruleset, identical to the JSON written
    /// into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "PROFESSIONAL_REORIENTATION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "registeredAtSiAisss", "value": true,
          "failCode": "PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-equals", "fact": "reorientationApprovedByCommission", "value": true,
          "failCode": "PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_APPROVED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "PROFESSIONAL_REORIENTATION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool registered, bool reorientationApproved)
        => new(new Dictionary<string, object?>
        {
            ["registeredAtSiAisss"] = registered,
            ["reorientationApprovedByCommission"] = reorientationApproved,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(registered: true, reorientationApproved: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROFESSIONAL_REORIENTATION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(1500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(registered: false, reorientationApproved: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_REGISTERED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(registered: true, reorientationApproved: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_APPROVED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(registered: false, reorientationApproved: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_REGISTERED");
        result.Value.ReasonCodes.Should().Contain("PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_APPROVED");
        result.Value.ReasonCodes.Should().NotContain("PROFESSIONAL_REORIENTATION_ELIGIBLE");
    }
}
