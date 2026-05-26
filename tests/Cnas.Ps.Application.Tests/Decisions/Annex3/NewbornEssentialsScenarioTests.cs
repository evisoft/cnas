using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.12-D — Ajutor pentru bunuri esențiale
/// (nou-născut). Verifies the 90-day claim window, the insured-parent gate,
/// and that the benefit is a fixed 2 500 MDL.
/// </summary>
public class NewbornEssentialsScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "NEWBORN_ESSENTIALS",
      "eligibility": [
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 90,
          "failCode": "NEWBORN_ESSENTIALS_INELIGIBLE_LATE_CLAIM" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "NEWBORN_ESSENTIALS_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "NEWBORN_ESSENTIALS_ELIGIBLE"
    }
    """;

    private static readonly DateTime BirthDate = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = BirthDate.AddDays(30);
    private static readonly DateTime LateClaim = BirthDate.AddDays(120);

    private static DecisionFacts Facts(DateTime claimDate, bool isInsured)
        => new(new Dictionary<string, object?>
        {
            ["birthDateUtc"] = BirthDate,
            ["claimDateUtc"] = claimDate,
            ["isInsured"] = isInsured,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(claimDate: OnTimeClaim, isInsured: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("NEWBORN_ESSENTIALS_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(claimDate: LateClaim, isInsured: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("NEWBORN_ESSENTIALS_INELIGIBLE_LATE_CLAIM");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(claimDate: OnTimeClaim, isInsured: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("NEWBORN_ESSENTIALS_INELIGIBLE_NOT_INSURED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(claimDate: LateClaim, isInsured: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("NEWBORN_ESSENTIALS_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().Contain("NEWBORN_ESSENTIALS_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().NotContain("NEWBORN_ESSENTIALS_ELIGIBLE");
    }
}
