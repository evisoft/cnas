using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.11-C — Ajutor de deces (accident de muncă).
/// Verifies the work-accident cause-of-death gate, the 365-day claim window,
/// and that the benefit is a fixed 5 000 MDL.
/// </summary>
public class WorkInjuryFuneralScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "WORK_INJURY_FUNERAL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deathFromWorkAccident", "value": true,
          "failCode": "WORK_INJURY_FUNERAL_INELIGIBLE_NOT_WORK_ACCIDENT" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "WORK_INJURY_FUNERAL_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 5000.00,
        "currency": "MDL"
      },
      "successCode": "WORK_INJURY_FUNERAL_ELIGIBLE"
    }
    """;

    private static readonly DateTime DeathDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = DeathDate.AddDays(60);
    private static readonly DateTime LateClaim = DeathDate.AddDays(400);

    private static DecisionFacts Facts(bool workAccident, DateTime claimDate)
        => new(new Dictionary<string, object?>
        {
            ["deathFromWorkAccident"] = workAccident,
            ["dateOfDeathUtc"] = DeathDate,
            ["claimDateUtc"] = claimDate,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: true, claimDate: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_FUNERAL_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(5000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: false, claimDate: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_FUNERAL_INELIGIBLE_NOT_WORK_ACCIDENT");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: true, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WORK_INJURY_FUNERAL_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(workAccident: false, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_FUNERAL_INELIGIBLE_NOT_WORK_ACCIDENT");
        result.Value.ReasonCodes.Should().Contain("WORK_INJURY_FUNERAL_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("WORK_INJURY_FUNERAL_ELIGIBLE");
    }
}
