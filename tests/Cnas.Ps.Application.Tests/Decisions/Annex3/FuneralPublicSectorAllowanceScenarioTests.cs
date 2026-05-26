using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.5-G — Ajutor de deces pentru angajații din
/// sectorul public (Public-sector funeral allowance). Verifies the public-sector
/// gate, the 1-year claim window, and that the benefit is a fixed 3 000 MDL.
/// </summary>
public class FuneralPublicSectorAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Public-Sector Funeral-Allowance ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "FUNERAL_PUBLIC_SECTOR",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedWasPublicSectorEmployee", "value": true,
          "failCode": "FUNERAL_PUBLIC_SECTOR_INELIGIBLE_NOT_PUBLIC" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "FUNERAL_PUBLIC_SECTOR_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3000.00,
        "currency": "MDL"
      },
      "successCode": "FUNERAL_PUBLIC_SECTOR_ELIGIBLE"
    }
    """;

    private static readonly DateTime DeathDate = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = DeathDate.AddDays(30);
    private static readonly DateTime LateClaim = DeathDate.AddDays(400);

    private static DecisionFacts Facts(bool deceasedWasPublicSector, DateTime claimDateUtc)
        => new(new Dictionary<string, object?>
        {
            ["deceasedWasPublicSectorEmployee"] = deceasedWasPublicSector,
            ["dateOfDeathUtc"] = DeathDate,
            ["claimDateUtc"] = claimDateUtc,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasPublicSector: true, claimDateUtc: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_PUBLIC_SECTOR_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasPublicSector: false, claimDateUtc: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_PUBLIC_SECTOR_INELIGIBLE_NOT_PUBLIC");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasPublicSector: true, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("FUNERAL_PUBLIC_SECTOR_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(deceasedWasPublicSector: false, claimDateUtc: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("FUNERAL_PUBLIC_SECTOR_INELIGIBLE_NOT_PUBLIC");
        result.Value.ReasonCodes.Should().Contain("FUNERAL_PUBLIC_SECTOR_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("FUNERAL_PUBLIC_SECTOR_ELIGIBLE");
    }
}
