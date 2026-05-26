using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.15-A — Pensie artist profesionist. Verifies
/// the artist-status gate, the 20-year career threshold, and that the benefit
/// is a fixed 3 500 MDL.
/// </summary>
public class ArtistPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "ARTIST_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProfessionalArtist", "value": true,
          "failCode": "ARTIST_PENSION_INELIGIBLE_NOT_ARTIST" },
        { "rule": "fact-greater-than", "fact": "artisticCareerYears", "value": 19,
          "failCode": "ARTIST_PENSION_INELIGIBLE_CAREER_YEARS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3500.00,
        "currency": "MDL"
      },
      "successCode": "ARTIST_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isArtist, int careerYears)
        => new(new Dictionary<string, object?>
        {
            ["wasProfessionalArtist"] = isArtist,
            ["artisticCareerYears"] = careerYears,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isArtist: true, careerYears: 25));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ARTIST_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isArtist: false, careerYears: 25));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ARTIST_PENSION_INELIGIBLE_NOT_ARTIST");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isArtist: true, careerYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ARTIST_PENSION_INELIGIBLE_CAREER_YEARS");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isArtist: false, careerYears: 5));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("ARTIST_PENSION_INELIGIBLE_NOT_ARTIST");
        result.Value.ReasonCodes.Should().Contain("ARTIST_PENSION_INELIGIBLE_CAREER_YEARS");
        result.Value.ReasonCodes.Should().NotContain("ARTIST_PENSION_ELIGIBLE");
    }
}
