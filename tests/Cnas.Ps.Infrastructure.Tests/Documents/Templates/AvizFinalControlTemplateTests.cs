using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AvizFinalControlTemplate"/> — the Annex 7
/// "Aviz final de control" (final audit / control closing opinion) template.
/// </summary>
public class AvizFinalControlTemplateTests
{
    /// <summary>Reference period start used across the suite.</summary>
    private static readonly DateTime PeriodStartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reference period end used across the suite.</summary>
    private static readonly DateTime PeriodEndUtc = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AvizFinalControlTemplate();

        template.TemplateCode.Should().Be(AvizFinalControlTemplate.Code);
        template.TemplateCode.Should().Be("aviz-final-control");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AvizFinalControlTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);

        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        doc.MainDocumentPart.Should().NotBeNull();
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        paragraphs.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_ContainsSubjectVerdictAndRecommendations()
    {
        var template = new AvizFinalControlTemplate();
        var facts = HappyPathFacts();
        facts["controlSubject"] = "Direcția Bălți — examen trimestrul I 2026";
        facts["verdict"] = "FAVORABIL";
        facts["recommendations"] = new List<string>
        {
            "Actualizarea registrului de cereri trimestrial.",
            "Instruirea suplimentară a inspectorilor noi.",
        };

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Direcția Bălți");
        text.Should().Contain("FAVORABIL");
        text.Should().Contain("Actualizarea registrului de cereri trimestrial");
        text.Should().Contain("Instruirea suplimentară a inspectorilor noi");
        text.Should().Contain("2026-01-01");
        text.Should().Contain("2026-03-31");
        text.Should().Contain("Petru Inspectorul");
        text.Should().Contain("AVIZ FINAL DE CONTROL");
    }

    [Fact]
    public void Render_MissingVerdict_ReturnsTemplateMissingFacts()
    {
        var template = new AvizFinalControlTemplate();
        var facts = HappyPathFacts();
        facts.Remove("verdict");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("verdict");
    }

    [Fact]
    public void Render_WithoutOptionalRecommendations_StillSucceeds()
    {
        // recommendations is optional — render must succeed without it. (No bullet list.)
        var template = new AvizFinalControlTemplate();
        var facts = HappyPathFacts();
        facts.Remove("recommendations");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Petru Inspectorul");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["controlSubject"] = "Subiect control",
        ["periodStartUtc"] = PeriodStartUtc,
        ["periodEndUtc"] = PeriodEndUtc,
        ["verdict"] = "FAVORABIL",
        ["conclusions"] = "Examinarea a confirmat conformitatea proceselor cu cerințele interne.",
        ["inspectorFullName"] = "Petru Inspectorul",
        ["recommendations"] = new List<string> { "Recomandare unu" },
    };
}
