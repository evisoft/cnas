using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="ScrisoareInformareTemplate"/> — the Annex 7
/// "Scrisoare de informare" (informational letter) template.
/// </summary>
public class ScrisoareInformareTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new ScrisoareInformareTemplate();

        template.TemplateCode.Should().Be(ScrisoareInformareTemplate.Code);
        template.TemplateCode.Should().Be("scrisoare-informare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new ScrisoareInformareTemplate();

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
    public void Render_RendersRecipientAndInformation()
    {
        var template = new ScrisoareInformareTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("mun. Chișinău, str. Pacii 10/2");
        text.Should().Contain("Vă informăm că dosarul Dvs.");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new ScrisoareInformareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("recipientFullName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("recipientFullName");
    }

    [Fact]
    public void Render_MissingInformationText_ReturnsTemplateMissingFacts()
    {
        var template = new ScrisoareInformareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("informationText");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("informationText");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["recipientFullName"] = "Ion Popescu",
        ["recipientAddress"] = "mun. Chișinău, str. Pacii 10/2",
        ["informationText"] = "Vă informăm că dosarul Dvs. a fost examinat în ședința comisiei.",
    };
}
