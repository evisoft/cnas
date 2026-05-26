using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="NotaInformareTemplate"/> — the Annex 7
/// "Notă de informare" (internal informational note attached to a dossier) template.
/// </summary>
/// <remarks>
/// Distinct from the citizen-facing <c>ScrisoareInformareTemplate</c>: the
/// <c>NotaInformare</c> is an internal artifact authored by an examiner / supervisor and
/// attached to the dossier for audit-trail purposes. It carries the author's name and role.
/// </remarks>
public class NotaInformareTemplateTests
{
    /// <summary>Reference UTC instant used across the suite.</summary>
    private static readonly DateTime AuthoredUtc = new(2026, 5, 12, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new NotaInformareTemplate();

        template.TemplateCode.Should().Be(NotaInformareTemplate.Code);
        template.TemplateCode.Should().Be("nota-informare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new NotaInformareTemplate();

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
    public void Render_ContainsAuthorAndContent()
    {
        var template = new NotaInformareTemplate();
        var facts = HappyPathFacts();
        facts["noteContent"] = "Beneficiarul a fost contactat telefonic și a confirmat datele de contact.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Beneficiarul a fost contactat telefonic");
        text.Should().Contain("Maria Examiner");
        text.Should().Contain("Inspector principal");
        text.Should().Contain("SQID-DOSS-789");
        text.Should().Contain("2026-05-12");
    }

    [Fact]
    public void Render_MissingNoteContent_ReturnsTemplateMissingFacts()
    {
        var template = new NotaInformareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("noteContent");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("noteContent");
    }

    [Fact]
    public void Render_WithoutOptionalReferenceCode_StillSucceeds()
    {
        // referenceCode is optional metadata — render must succeed without it.
        var template = new NotaInformareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("referenceCode");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Maria Examiner");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["dossierSqid"] = "SQID-DOSS-789",
        ["authorFullName"] = "Maria Examiner",
        ["authorRole"] = "Inspector principal",
        ["authoredUtc"] = AuthoredUtc,
        ["noteContent"] = "Conținut notă",
        ["referenceCode"] = "NOTA-2026-0042",
    };
}
