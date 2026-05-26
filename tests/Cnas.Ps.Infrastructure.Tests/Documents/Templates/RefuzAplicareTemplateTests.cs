using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="RefuzAplicareTemplate"/> — Annex 7 "Refuz al cererii" template.
/// </summary>
public class RefuzAplicareTemplateTests
{
    private static readonly DateTime DecisionUtc = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_HappyPath_ProducesDocxWithMagicBytes()
    {
        var template = new RefuzAplicareTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_ContainsRefuseReasonAndDecisionDate()
    {
        var template = new RefuzAplicareTemplate();
        var facts = HappyPathFacts();
        facts["refuseReason"] = "Lipsa actelor doveditoare ale stagiului de cotizare.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Lipsa actelor doveditoare");
        text.Should().Contain("2026-05-19");
        text.Should().Contain("Ion Popescu");
    }

    [Fact]
    public void Render_MissingRefuseReason_ReturnsTemplateMissingFacts()
    {
        var template = new RefuzAplicareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("refuseReason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["dossierSqid"] = "SQID-DOSS-99",
        ["refuseReason"] = "Motiv refuz",
        ["decisionUtc"] = DecisionUtc,
    };
}
