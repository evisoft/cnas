using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DispozitieRecalculTemplate"/> — the Annex 7
/// "Dispoziție de recalcul" (recalculation disposition) template.
/// </summary>
public class DispozitieRecalculTemplateTests
{
    /// <summary>Reference UTC effective-from instant used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DispozitieRecalculTemplate();

        template.TemplateCode.Should().Be(DispozitieRecalculTemplate.Code);
        template.TemplateCode.Should().Be("dispozitie-recalcul");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DispozitieRecalculTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);

        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        paragraphs.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new DispozitieRecalculTemplate();
        var facts = HappyPathFacts();
        facts.Remove("reason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("reason");
    }

    [Fact]
    public void Render_MoneyFormattedCorrectly()
    {
        var template = new DispozitieRecalculTemplate();
        var facts = HappyPathFacts();
        facts["previousAmountMdl"] = 1500.00m;
        facts["newAmountMdl"] = 1800.50m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("1,500.00 MDL");
        text.Should().Contain("1,800.50 MDL");
        // Effective-from rendered as UTC date.
        text.Should().Contain("2026-04-01");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["reason"] = "Indexare anuală conform Hotărârii de Guvern nr. 100/2026.",
        ["previousAmountMdl"] = 2500.00m,
        ["newAmountMdl"] = 2700.00m,
        ["effectiveFromUtc"] = EffectiveFromUtc,
    };
}
