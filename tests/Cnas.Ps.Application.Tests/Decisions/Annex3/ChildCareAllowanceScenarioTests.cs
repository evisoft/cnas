using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.1-B — Indemnizație lunară pentru îngrijirea copilului
/// până la vârsta de 3 ani. These tests load the JSON ruleset published by the engine
/// in <c>ChildCareAllowanceJson</c> and exercise <see cref="IDecisionEngine"/> against
/// representative real-world scenarios.
/// </summary>
public class ChildCareAllowanceScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    /// <summary>
    /// Canonical Child-Care Allowance ruleset, identical to the JSON written into the
    /// <c>ServicePassport.DecisionRulesJson</c> seed row by the infrastructure layer.
    /// Kept inline so this test does not depend on Infrastructure.
    /// </summary>
    private const string ChildCareAllowanceJson = """
    {
      "code": "CHILD_CARE_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "CHILD_CARE_INELIGIBLE_NOT_INSURED" },
        { "rule": "age-at-date-between", "dobFact": "childDateOfBirthUtc",
          "referenceFact": "claimDateUtc", "min": 0, "max": 2,
          "failCode": "CHILD_CARE_INELIGIBLE_CHILD_TOO_OLD" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 30,
        "referenceFact": "referenceSalaryMdl"
      },
      "successCode": "CHILD_CARE_ELIGIBLE"
    }
    """;

    private static readonly DateTime ChildDob = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimWhenInfant = ChildDob.AddMonths(6);
    private static readonly DateTime ClaimWhenChildIsFour = ChildDob.AddYears(4);

    private static DecisionFacts Facts(
        bool isInsured,
        DateTime claimDateUtc,
        Money referenceSalary)
        => new(new Dictionary<string, object?>
        {
            ["isInsured"] = isInsured,
            ["childDateOfBirthUtc"] = ChildDob,
            ["claimDateUtc"] = claimDateUtc,
            ["referenceSalaryMdl"] = referenceSalary,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(
            ChildCareAllowanceJson,
            Facts(isInsured: true, claimDateUtc: ClaimWhenInfant, referenceSalary: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARE_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(2400m)); // 30% of 8000
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            ChildCareAllowanceJson,
            Facts(isInsured: false, claimDateUtc: ClaimWhenInfant, referenceSalary: Money.Mdl(8000m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARE_INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(
            ChildCareAllowanceJson,
            Facts(isInsured: true, claimDateUtc: ClaimWhenChildIsFour, referenceSalary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("CHILD_CARE_INELIGIBLE_CHILD_TOO_OLD");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(
            ChildCareAllowanceJson,
            Facts(isInsured: false, claimDateUtc: ClaimWhenChildIsFour, referenceSalary: Money.Mdl(8000m)));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("CHILD_CARE_INELIGIBLE_NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("CHILD_CARE_INELIGIBLE_CHILD_TOO_OLD");
        result.Value.ReasonCodes.Should().NotContain("CHILD_CARE_ELIGIBLE");
    }
}
