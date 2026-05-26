using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaInstitutieMedicalaTemplate"/> — the Annex 7
/// "Adresă către instituție medicală" (letter to a medical institution) template.
/// </summary>
public class AdresaInstitutieMedicalaTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaInstitutieMedicalaTemplate();

        template.TemplateCode.Should().Be(AdresaInstitutieMedicalaTemplate.Code);
        template.TemplateCode.Should().Be("adresa-institutie-medicala");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaInstitutieMedicalaTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        // ZIP local-file-header signature — DOCX is a ZIP envelope.
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
    public void Render_RendersInstitutionAndRequestAndAttachedDocs()
    {
        var template = new AdresaInstitutieMedicalaTemplate();
        var docs = new List<string>
        {
            "Copia fișei medicale",
            "Rezultatele analizelor de laborator",
            "Concluziile examinării radiologice",
        };
        var facts = HappyPathFacts();
        facts["attachedDocs"] = docs;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("IMSP Spitalul Clinic Republican");
        text.Should().Contain("mun. Chișinău, str. Testemițanu 29");
        text.Should().Contain("Vă rugăm să transmiteți");
        foreach (var d in docs)
        {
            text.Should().Contain(d);
        }
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaInstitutieMedicalaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("institutionName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("institutionName");
    }

    [Fact]
    public void Render_MissingAttachedDocs_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaInstitutieMedicalaTemplate();
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
        ["institutionName"] = "IMSP Spitalul Clinic Republican",
        ["institutionAddress"] = "mun. Chișinău, str. Testemițanu 29",
        ["requestText"] = "Vă rugăm să transmiteți copia fișei medicale a pacientului în cauză.",
        ["attachedDocs"] = new List<string> { "Cererea solicitantului", "Copia buletinului" },
    };
}
