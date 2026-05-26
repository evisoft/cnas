using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions.Annex3;

/// <summary>
/// Acceptance tests for Annex 3.15-B — Pensie scriitor profesionist. Verifies
/// the writer-status gate, the Writers' Union recognition gate, and that the
/// benefit is a fixed 3 200 MDL.
/// </summary>
public class WriterPensionScenarioTests
{
    private static readonly IDecisionEngine Engine = new JsonRulesDecisionEngine();

    private const string Json = """
    {
      "code": "WRITER_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProfessionalWriter", "value": true,
          "failCode": "WRITER_PENSION_INELIGIBLE_NOT_WRITER" },
        { "rule": "fact-equals", "fact": "recognitionByUnion", "value": true,
          "failCode": "WRITER_PENSION_INELIGIBLE_NOT_RECOGNIZED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3200.00,
        "currency": "MDL"
      },
      "successCode": "WRITER_PENSION_ELIGIBLE"
    }
    """;

    private static DecisionFacts Facts(bool isWriter, bool recognized)
        => new(new Dictionary<string, object?>
        {
            ["wasProfessionalWriter"] = isWriter,
            ["recognitionByUnion"] = recognized,
        });

    [Fact]
    public void Happy_Path_EligibleWithExpectedAmount()
    {
        var result = Engine.Evaluate(Json, Facts(isWriter: true, recognized: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WRITER_PENSION_ELIGIBLE");
        result.Value.Amount.Should().Be(Money.Mdl(3200m));
    }

    [Fact]
    public void Ineligible_PrimaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isWriter: false, recognized: true));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WRITER_PENSION_INELIGIBLE_NOT_WRITER");
        result.Value.Amount.Should().BeNull();
    }

    [Fact]
    public void Ineligible_SecondaryReason_FailsWithCode()
    {
        var result = Engine.Evaluate(Json, Facts(isWriter: true, recognized: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().ContainSingle().Which.Should().Be("WRITER_PENSION_INELIGIBLE_NOT_RECOGNIZED");
    }

    [Fact]
    public void Both_Failures_AccumulateReasonCodes()
    {
        var result = Engine.Evaluate(Json, Facts(isWriter: false, recognized: false));

        result.Value.IsEligible.Should().BeFalse();
        result.Value.ReasonCodes.Should().Contain("WRITER_PENSION_INELIGIBLE_NOT_WRITER");
        result.Value.ReasonCodes.Should().Contain("WRITER_PENSION_INELIGIBLE_NOT_RECOGNIZED");
        result.Value.ReasonCodes.Should().NotContain("WRITER_PENSION_ELIGIBLE");
    }
}
