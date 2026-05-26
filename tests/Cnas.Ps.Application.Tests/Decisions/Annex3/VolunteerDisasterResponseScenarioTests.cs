using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.9-D — Indemnizație pentru voluntari de
/// intervenție (Volunteer disaster-response indemnity). Verifies the
/// volunteer-status gate, the disability-from-volunteering gate, and that the
/// benefit is a fixed 2 500 MDL.
/// </summary>
public class VolunteerDisasterResponseScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "VOLUNTEER_DISASTER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasDisasterVolunteer", "value": true,
          "failCode": "VOLUNTEER_DISASTER_INELIGIBLE_NOT_VOLUNTEER" },
        { "rule": "fact-equals", "fact": "disabilityFromVolunteering", "value": true,
          "failCode": "VOLUNTEER_DISASTER_INELIGIBLE_NO_DISABILITY" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "VOLUNTEER_DISASTER_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool wasVolunteer, bool disability)
        => new(new Dictionary<string, object?>
        {
            ["wasDisasterVolunteer"] = wasVolunteer,
            ["disabilityFromVolunteering"] = disability,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(wasVolunteer: true, disability: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("VOLUNTEER_DISASTER_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasVolunteer: false, disability: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("VOLUNTEER_DISASTER_INELIGIBLE_NOT_VOLUNTEER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(wasVolunteer: true, disability: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("VOLUNTEER_DISASTER_INELIGIBLE_NO_DISABILITY");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(wasVolunteer: false, disability: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("VOLUNTEER_DISASTER_INELIGIBLE_NOT_VOLUNTEER");
        result.Value.ReasonCodes.Should().Contain("VOLUNTEER_DISASTER_INELIGIBLE_NO_DISABILITY");
        result.Value.ReasonCodes.Should().NotContain("VOLUNTEER_DISASTER_ELIGIBLE");
    }
}
