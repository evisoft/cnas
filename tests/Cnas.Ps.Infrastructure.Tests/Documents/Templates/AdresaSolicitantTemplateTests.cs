using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaSolicitantTemplate"/> — the Annex 7
/// "Adresă către solicitant" (formal letter to applicant) template.
/// </summary>
public class AdresaSolicitantTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaSolicitantTemplate();

        template.TemplateCode.Should().Be(AdresaSolicitantTemplate.Code);
        template.TemplateCode.Should().Be("adresa-solicitant");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaSolicitantTemplate();

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
    public void Render_RendersRecipientSubjectAndBody()
    {
        var template = new AdresaSolicitantTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("mun. Chișinău, str. Pacii 10/2");
        text.Should().Contain("Solicitarea Dvs. nr. 1234/2026");
        text.Should().Contain("vă comunicăm că cererea Dvs. a fost recepționată");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaSolicitantTemplate();
        var facts = HappyPathFacts();
        facts.Remove("subject");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("subject");
    }

    [Fact]
    public void Render_MissingBodyText_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaSolicitantTemplate();
        var facts = HappyPathFacts();
        facts.Remove("bodyText");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("bodyText");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryFullName"] = "Ion Popescu",
        ["beneficiaryAddress"] = "mun. Chișinău, str. Pacii 10/2",
        ["subject"] = "Solicitarea Dvs. nr. 1234/2026",
        ["bodyText"] = "Stimate domn, vă comunicăm că cererea Dvs. a fost recepționată și va fi examinată în termenul prevăzut.",
    };
}
