using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.2-D — Pensie de invaliditate pentru veteranii de
/// război (War-veteran invalidity pension). Verifies the veteran-status gate, the
/// disability-degree gate, and that the benefit amount is keyed by disability
/// degree.
/// </summary>
public class WarVeteranInvalidityPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical War-Veteran Invalidity Pension ruleset, identical to the JSON
    /// written into the <c>ServicePassport.DecisionRulesJson</c> seed row.
    /// </summary>
    private const string Json = """
    {
      "code": "WAR_VETERAN_INVALIDITY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "WAR_VETERAN_INVALIDITY_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "WAR_VETERAN_INVALIDITY_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3500.00,
          "accentuated": 2700.00,
          "medium":      2000.00
        }
      },
      "successCode": "WAR_VETERAN_INVALIDITY_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isWarVeteran, string disabilityDegree)
        => new(new Dictionary<string, object?>
        {
            ["isWarVeteran"] = isWarVeteran,
            ["disabilityDegree"] = disabilityDegree,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isWarVeteran: true, disabilityDegree: "severe"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_VETERAN_INVALIDITY_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3500m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isWarVeteran: false, disabilityDegree: "severe"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_VETERAN_INVALIDITY_INELIGIBLE_NOT_VETERAN");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isWarVeteran: true, disabilityDegree: "none"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WAR_VETERAN_INVALIDITY_INELIGIBLE_DEGREE");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isWarVeteran: false, disabilityDegree: "none"));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WAR_VETERAN_INVALIDITY_INELIGIBLE_NOT_VETERAN");
        result.Value.ReasonCodes.Should().Contain("WAR_VETERAN_INVALIDITY_INELIGIBLE_DEGREE");
        result.Value.ReasonCodes.Should().NotContain("WAR_VETERAN_INVALIDITY_ELIGIBLE");
    }
}
