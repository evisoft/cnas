using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AvizAvocatuluiPoporuluiTemplate"/> — the Annex 7
/// "Aviz pentru Avocatul Poporului" (legal notice to the Ombudsperson) template.
/// </summary>
public class AvizAvocatuluiPoporuluiTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AvizAvocatuluiPoporuluiTemplate();

        template.TemplateCode.Should().Be(AvizAvocatuluiPoporuluiTemplate.Code);
        template.TemplateCode.Should().Be("aviz-avocatul-poporului");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AvizAvocatuluiPoporuluiTemplate();

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
    public void Render_RendersGreetingDossierAndAttachedDocs()
    {
        var template = new AvizAvocatuluiPoporuluiTemplate();
        var attached = new List<string>
        {
            "Copia cererii inițiale",
            "Decizia atacată",
            "Corespondența cu solicitantul",
        };
        var facts = HappyPathFacts();
        facts["attachedDocs"] = attached;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        // Pre-encoded Sqid must appear verbatim (RULE 3 — never decode in templates).
        text.Should().Contain("SQID-DOSS-77");
        text.Should().Contain("Avocatul Poporului");
        foreach (var doc in attached)
        {
            text.Should().Contain(doc);
        }
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AvizAvocatuluiPoporuluiTemplate();
        var facts = HappyPathFacts();
        facts.Remove("caseSummary");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("caseSummary");
    }

    [Fact]
    public void Render_MissingAttachedDocs_ReturnsTemplateMissingFacts()
    {
        var template = new AvizAvocatuluiPoporuluiTemplate();
        var facts = HappyPathFacts();
        facts.Remove("attachedDocs");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("attachedDocs");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["dossierSqid"] = "SQID-DOSS-77",
        ["caseSummary"] = "Solicitantul a contestat decizia CNAS din 12 martie 2026 privind cuantumul pensiei.",
        ["attachedDocs"] = new List<string> { "Document A", "Document B" },
    };
}
