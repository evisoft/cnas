using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions;

/// <summary>
/// Behaviour tests for <see cref="JsonRulesDecisionEngine"/> — the JSON-driven engine
/// powering the 81+ life-event services. Each rule kind has at least one pass + one
/// fail scenario; amount kinds, malformed JSON, and accumulation are covered.
/// </summary>
public class JsonRulesDecisionEngineTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private static DecisionFacts Facts(Dictionary<string, object?> dict) => new(dict);

    // ───────────────────────────────── Eligibility: fact-equals ─────────────────────────────────

    [Fact]
    public void FactEquals_Pass_BoolFactMatches()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "isInsured", "value": true,
              "failCode": "INELIGIBLE_NOT_INSURED" }
          ],
          "amount": { "kind": "fixed", "value": 100, "currency": "MDL" },
          "successCode": "ELIGIBLE"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["isInsured"] = true }));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(100m));
    }

    [Fact]
    public void FactEquals_Fail_BoolFactMismatch()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "isInsured", "value": true,
              "failCode": "INELIGIBLE_NOT_INSURED" }
          ],
          "amount": { "kind": "fixed", "value": 100, "currency": "MDL" },
          "successCode": "ELIGIBLE"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["isInsured"] = false }));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("INELIGIBLE_NOT_INSURED");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void FactEquals_StringFactPasses()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "status", "value": "ACTIVE",
              "failCode": "INELIGIBLE_INACTIVE" }
          ],
          "amount": { "kind": "fixed", "value": 50, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["status"] = "ACTIVE" }));

        result.Value.IsEligible.Should().BeTrue();
    }

    // ───────────────────────────────── Eligibility: numeric comparisons ─────────────────────────

    [Fact]
    public void FactGreaterThan_Pass_NumericAboveThreshold()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-greater-than", "fact": "income", "value": 1000,
              "failCode": "INCOME_TOO_LOW" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["income"] = 1500m }));

        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void FactGreaterThan_Fail_NumericAtOrBelowThreshold()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-greater-than", "fact": "income", "value": 1000,
              "failCode": "INCOME_TOO_LOW" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["income"] = 1000m }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("INCOME_TOO_LOW");
    }

    [Fact]
    public void FactLessThan_Pass()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-less-than", "fact": "age", "value": 18,
              "failCode": "TOO_OLD" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["age"] = 10 }));

        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void FactLessThan_Fail()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-less-than", "fact": "age", "value": 18,
              "failCode": "TOO_OLD" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["age"] = 25 }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("TOO_OLD");
    }

    // ───────────────────────────────── Eligibility: fact-in-set ────────────────────────────────

    [Fact]
    public void FactInSet_Pass_ValueIsMember()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-in-set", "fact": "category", "values": ["A", "B", "C"],
              "failCode": "BAD_CATEGORY" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["category"] = "B" }));

        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void FactInSet_Fail_ValueNotMember()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-in-set", "fact": "category", "values": ["A", "B", "C"],
              "failCode": "BAD_CATEGORY" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["category"] = "Z" }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("BAD_CATEGORY");
    }

    // ───────────────────────────────── Eligibility: date-within-days ───────────────────────────

    [Fact]
    public void DateWithinDays_Pass_WithinWindow()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "date-within-days", "fact": "birthDateUtc",
              "referenceFact": "claimDateUtc", "maxDays": 365,
              "failCode": "LATE_CLAIM" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var birth = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var claim = birth.AddDays(100);

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["birthDateUtc"] = birth,
            ["claimDateUtc"] = claim,
        }));

        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void DateWithinDays_Fail_OutsideWindow()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "date-within-days", "fact": "birthDateUtc",
              "referenceFact": "claimDateUtc", "maxDays": 365,
              "failCode": "LATE_CLAIM" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var birth = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var claim = birth.AddDays(400);

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["birthDateUtc"] = birth,
            ["claimDateUtc"] = claim,
        }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("LATE_CLAIM");
    }

    // ───────────────────────────────── Eligibility: age-at-date-between ────────────────────────

    [Fact]
    public void AgeAtDateBetween_Pass_Adult()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "age-at-date-between", "dobFact": "dob",
              "referenceFact": "today", "min": 18, "max": 65,
              "failCode": "WRONG_AGE" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var dob = new DateTime(1990, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["dob"] = dob,
            ["today"] = today,
        }));

        result.Value.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void AgeAtDateBetween_Fail_TooYoung()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "age-at-date-between", "dobFact": "dob",
              "referenceFact": "today", "min": 18, "max": 65,
              "failCode": "WRONG_AGE" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var dob = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["dob"] = dob,
            ["today"] = today,
        }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WRONG_AGE");
    }

    // ───────────────────────────────── Amount kinds ────────────────────────────────────────────

    [Fact]
    public void Amount_Fixed_ProducesExpectedMoney()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": { "kind": "fixed", "value": 1234.50, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(1234.50m));
    }

    [Fact]
    public void Amount_Table_HitReturnsMappedValue()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": {
            "kind": "table",
            "lookupFact": "tier",
            "currency": "MDL",
            "table": { "1": 100, "2": 200, "default": 50 }
          },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["tier"] = 2 }));

        result.Value.Amount.Should().Be(Money.Mdl(200m));
    }

    [Fact]
    public void Amount_Table_MissReturnsDefault()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": {
            "kind": "table",
            "lookupFact": "tier",
            "currency": "MDL",
            "table": { "1": 100, "2": 200, "default": 50 }
          },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new() { ["tier"] = 99 }));

        result.Value.Amount.Should().Be(Money.Mdl(50m));
    }

    [Fact]
    public void Amount_PercentOfFact_AppliesRate()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": {
            "kind": "percent-of-fact",
            "percent": 25,
            "referenceFact": "salary"
          },
          "successCode": "OK"
        }
        """;

        var salary = Money.Mdl(8000m);
        var result = Engine.Evaluate(json, Facts(new() { ["salary"] = salary }));

        result.Value.Amount.Should().Be(Money.Mdl(2000m));
    }

    // ───────────────────────────────── Accumulation ────────────────────────────────────────────

    [Fact]
    public void Accumulation_TwoFailingRules_BothCodesAccumulate()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "isInsured", "value": true,
              "failCode": "NOT_INSURED" },
            { "rule": "fact-less-than", "fact": "age", "value": 50,
              "failCode": "TOO_OLD" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["isInsured"] = false,
            ["age"] = 70,
        }));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("NOT_INSURED");
        result.Value.ReasonCodes.Should().Contain("TOO_OLD");
    }

    [Fact]
    public void HappyPath_MultipleRulesAndTableLookup_ProducesAmount()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "isInsured", "value": true,
              "failCode": "NOT_INSURED" },
            { "rule": "fact-greater-than", "fact": "income", "value": 500,
              "failCode": "INCOME_TOO_LOW" }
          ],
          "amount": {
            "kind": "table",
            "lookupFact": "tier",
            "currency": "MDL",
            "table": { "A": 1000, "B": 2000, "default": 500 }
          },
          "successCode": "ELIGIBLE"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()
        {
            ["isInsured"] = true,
            ["income"] = 1000m,
            ["tier"] = "B",
        }));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(2000m));
        result.Value.ReasonCodes.Should().Contain("ELIGIBLE");
    }

    // ───────────────────────────────── Failure modes ───────────────────────────────────────────

    [Fact]
    public void BadRule_UnknownRuleKind_ReturnsBadRule()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "unknown-rule-kind", "fact": "x", "failCode": "FAIL" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void BadRule_MalformedJson_ReturnsBadRule()
    {
        const string json = "{ this is not valid json";

        var result = Engine.Evaluate(json, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void BadRule_MissingAmountSection_ReturnsBadRule()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void BadRule_UnknownAmountKind_ReturnsAmountComputationFailed()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": { "kind": "from-thin-air" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AmountComputationFailed);
    }

    [Fact]
    public void MissingFact_RuleReferencesAbsentFact_FailsAsBadRule()
    {
        // The rule asks for a fact that the caller didn't supply.
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-equals", "fact": "isInsured", "value": true,
              "failCode": "NOT_INSURED" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MissingFact);
    }

    [Fact]
    public void EmptyEligibility_AllPasses_ReturnsAmount()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [],
          "amount": { "kind": "fixed", "value": 42, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        var result = Engine.Evaluate(json, Facts(new()));

        result.Value.IsEligible.Should().BeTrue();
        result.Value.Amount.Should().Be(Money.Mdl(42m));
    }

    [Fact]
    public void NullJson_ReturnsBadRule()
    {
        var result = Engine.Evaluate(null!, Facts(new()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void NullFacts_Throws()
    {
        Action act = () => Engine.Evaluate("{}", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ───────────────────────── Precision: double/float refused at boundary ─────────────────────

    /// <summary>
    /// Lossy-cast guard: a <see cref="double"/>-typed fact must NOT be silently rounded
    /// to <see cref="decimal"/>. The classic example
    /// (<c>0.1 + 0.2 = 0.30000000000000004</c>) would slip past a naive
    /// <c>(decimal)dbl</c> cast and flip an eligibility decision on the boundary.
    /// The engine now refuses double/float fact values and surfaces
    /// <see cref="ErrorCodes.BadRule"/> so the caller knows to pre-convert at the
    /// fact-collection boundary.
    /// </summary>
    [Fact]
    public void FactGreaterThan_DoubleFact_ReturnsBadRule()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-greater-than", "fact": "income", "value": 0.3,
              "failCode": "INCOME_TOO_LOW" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        // The textbook double-precision artifact: 0.1 + 0.2 in IEEE-754 is NOT 0.3.
        var lossy = 0.1 + 0.2; // 0.30000000000000004
        var result = Engine.Evaluate(json, Facts(new() { ["income"] = lossy }));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    /// <summary>
    /// Same guarantee as the double test, this time for <see cref="float"/>. The two
    /// types share the lossy-cast hazard so we pin both branches of the switch.
    /// </summary>
    [Fact]
    public void FactGreaterThan_FloatFact_ReturnsBadRule()
    {
        const string json = """
        {
          "code": "TEST",
          "eligibility": [
            { "rule": "fact-greater-than", "fact": "ratio", "value": 0.5,
              "failCode": "TOO_LOW" }
          ],
          "amount": { "kind": "fixed", "value": 1, "currency": "MDL" },
          "successCode": "OK"
        }
        """;

        float f = 0.6f;
        var result = Engine.Evaluate(json, Facts(new() { ["ratio"] = f }));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }
}
