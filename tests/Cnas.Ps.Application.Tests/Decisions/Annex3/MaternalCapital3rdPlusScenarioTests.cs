using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.17-B — Capital matern (al treilea copil sau
/// mai mult). Verifies the insured-parent gate, the birth-order &gt; 2 gate,
/// the 365-day claim window, and that the benefit is a fixed 35 000 MDL. This
/// passport has three eligibility rules, hence the additional tertiary-refusal
/// test.
/// </summary>
public class MaternalCapital3rdPlusScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "MATERNAL_CAPITAL_3P",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-greater-than", "fact": "birthOrder", "value": 2,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_BIRTH_ORDER" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 35000.00,
        "currency": "MDL"
      },
      "successCode": "MATERNAL_CAPITAL_3P_ELIGIBLE"
    }
    """;

    private static readonly DateTime BirthDate = new(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTimeClaim = BirthDate.AddDays(90);
    private static readonly DateTime LateClaim = BirthDate.AddDays(400);

    private static DecisionFacts Facts(bool isInsured, int birthOrder, DateTime claimDate)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["birthOrder"] = birthOrder,
            ["birthDateUtc"] = BirthDate,
            ["claimDateUtc"] = claimDate,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: true, birthOrder: 3, claimDate: OnTimeClaim));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNAL_CAPITAL_3P_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(35000m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: false, birthOrder: 3, claimDate: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNAL_CAPITAL_3P_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: true, birthOrder: 2, claimDate: OnTimeClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNAL_CAPITAL_3P_INELIGIBLE_BIRTH_ORDER");
    }

    [Fact]
    public void Ineligible_TertiaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: true, birthOrder: 3, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("MATERNAL_CAPITAL_3P_INELIGIBLE_LATE_CLAIM");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isInsured: false, birthOrder: 1, claimDate: LateClaim));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("MATERNAL_CAPITAL_3P_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("MATERNAL_CAPITAL_3P_INELIGIBLE_BIRTH_ORDER");
        result.Value.ReasonCodes.Should().Contain("MATERNAL_CAPITAL_3P_INELIGIBLE_LATE_CLAIM");
        result.Value.ReasonCodes.Should().NotContain("MATERNAL_CAPITAL_3P_ELIGIBLE");
    }
}
