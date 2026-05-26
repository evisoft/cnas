using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DispozitieReluareTemplate"/> — the Annex 7
/// "Dispoziție de reluare a plății" (payment-resumption disposition) template.
/// </summary>
public class DispozitieReluareTemplateTests
{
    /// <summary>Reference UTC resumption effective-from used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DispozitieReluareTemplate();

        template.TemplateCode.Should().Be(DispozitieReluareTemplate.Code);
        template.TemplateCode.Should().Be("dispozitie-reluare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DispozitieReluareTemplate();

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
        // The two-row reluare table must be present.
        var tables = doc.MainDocumentPart!.Document.Body!.Descendants<Table>().ToList();
        tables.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new DispozitieReluareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("reason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("reason");
    }

    [Fact]
    public void Render_MissingRestoredAmount_ReturnsTemplateMissingFacts()
    {
        var template = new DispozitieReluareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("restoredAmountMdl");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("restoredAmountMdl");
    }

    [Fact]
    public void Render_MoneyFormattedCorrectly()
    {
        var template = new DispozitieReluareTemplate();
        var facts = HappyPathFacts();
        facts["restoredAmountMdl"] = 2750.50m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2,750.50 MDL");
        // Effective-from UTC date.
        text.Should().Contain("2026-06-01");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("Reluare în urma confirmării");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["reason"] = "Reluare în urma confirmării existenței beneficiarului.",
        ["restoredAmountMdl"] = 2500.00m,
        ["effectiveFromUtc"] = EffectiveFromUtc,
    };
}
