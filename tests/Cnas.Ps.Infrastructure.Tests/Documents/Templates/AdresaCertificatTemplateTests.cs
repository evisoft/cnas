using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaCertificatTemplate"/> — the Annex 7
/// "Adresă certificat" (ad-hoc certificate letter shell) template.
/// </summary>
public class AdresaCertificatTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaCertificatTemplate();

        template.TemplateCode.Should().Be(AdresaCertificatTemplate.Code);
        template.TemplateCode.Should().Be("adresa-certificat");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaCertificatTemplate();

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
    public void Render_RendersRecipientSubjectBodyAndSignatory()
    {
        var template = new AdresaCertificatTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Maria Ionescu");
        text.Should().Contain("mun. Chișinău, str. Independenței 5");
        text.Should().Contain("Confirmarea statutului de pensionar");
        text.Should().Contain("Prin prezenta vă confirmăm");
        text.Should().Contain("Vasile Cebotari");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaCertificatTemplate();
        var facts = HappyPathFacts();
        facts.Remove("subject");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("subject");
    }

    [Fact]
    public void Render_MissingSignatoryName_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaCertificatTemplate();
        var facts = HappyPathFacts();
        facts.Remove("signatoryName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("signatoryName");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["recipientFullName"] = "Maria Ionescu",
        ["recipientAddress"] = "mun. Chișinău, str. Independenței 5",
        ["subject"] = "Confirmarea statutului de pensionar",
        ["bodyText"] = "Prin prezenta vă confirmăm statutul Dvs. de pensionar al CNAS.",
        ["signatoryName"] = "Vasile Cebotari",
    };
}
