using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaIntrebareSuplimentaraTemplate"/> — the Annex 7
/// "Adresă pentru întrebare suplimentară" (formal clarifying-question letter) template.
/// </summary>
/// <remarks>
/// Tests follow the established suite pattern: code-property identity, ZIP-magic
/// happy-path, structural OpenXML smoke, placeholder substitution via the shared
/// <see cref="DeciziaPensieTemplateTests.ExtractAllText"/> helper, missing-required-fact
/// path, and optional-field-omitted resilience.
/// </remarks>
public class AdresaIntrebareSuplimentaraTemplateTests
{
    /// <summary>Reference UTC reply-by date used across the suite.</summary>
    private static readonly DateTime ReplyByUtc = new(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaIntrebareSuplimentaraTemplate();

        template.TemplateCode.Should().Be(AdresaIntrebareSuplimentaraTemplate.Code);
        template.TemplateCode.Should().Be("adresa-intrebare-suplimentara");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaIntrebareSuplimentaraTemplate();

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
        doc.MainDocumentPart.Should().NotBeNull();
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        paragraphs.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_ContainsSubjectAndQuestionAndDeadline()
    {
        var template = new AdresaIntrebareSuplimentaraTemplate();
        var facts = HappyPathFacts();
        facts["subject"] = "Confirmare adresă de domiciliu";
        facts["questionText"] = "Vă rugăm să confirmați adresa de domiciliu indicată la momentul depunerii cererii.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Confirmare adresă de domiciliu");
        text.Should().Contain("Vă rugăm să confirmați adresa de domiciliu");
        text.Should().Contain("2026-08-15");
        text.Should().Contain("Vasile Munteanu");
        text.Should().Contain("ADRESĂ — ÎNTREBARE SUPLIMENTARĂ");
    }

    [Fact]
    public void Render_MissingQuestionText_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaIntrebareSuplimentaraTemplate();
        var facts = HappyPathFacts();
        facts.Remove("questionText");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("questionText");
    }

    [Fact]
    public void Render_WithoutOptionalReplyChannel_StillSucceeds()
    {
        // replyChannel is optional metadata — render must succeed without it.
        var template = new AdresaIntrebareSuplimentaraTemplate();
        var facts = HappyPathFacts();
        facts.Remove("replyChannel");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Vasile Munteanu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000018",
        ["beneficiaryFullName"] = "Vasile Munteanu",
        ["dossierSqid"] = "SQID-DOSS-AISUPL",
        ["subject"] = "Subiect întrebare",
        ["questionText"] = "Conținut întrebare",
        ["replyByUtc"] = ReplyByUtc,
        ["replyChannel"] = "prin scrisoare la sediul CNAS",
    };
}
