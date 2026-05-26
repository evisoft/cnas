using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AnsamblulAcordareDrepturiTemplate"/> — the Annex 7
/// "Ansamblul de acordare drepturi" (package of granted rights) template.
/// </summary>
public class AnsamblulAcordareDrepturiTemplateTests
{
    /// <summary>Reference UTC effective-from date used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AnsamblulAcordareDrepturiTemplate();

        template.TemplateCode.Should().Be(AnsamblulAcordareDrepturiTemplate.Code);
        template.TemplateCode.Should().Be("ansamblul-acordare-drepturi");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AnsamblulAcordareDrepturiTemplate();

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
    public void Render_RendersBeneficiaryAndRightsAndEffectiveFrom()
    {
        var template = new AnsamblulAcordareDrepturiTemplate();
        var rights = new List<string>
        {
            "Dreptul la pensie pentru limită de vârstă",
            "Dreptul la indemnizație lunară",
            "Dreptul la reabilitare medicală",
        };
        var facts = HappyPathFacts();
        facts["rights"] = rights;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2000000000007");
        text.Should().Contain("Ion Popescu");
        foreach (var r in rights)
        {
            text.Should().Contain(r);
        }
        // Effective-from as UTC date.
        text.Should().Contain("2026-06-01");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AnsamblulAcordareDrepturiTemplate();
        var facts = HappyPathFacts();
        facts.Remove("beneficiaryFullName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("beneficiaryFullName");
    }

    [Fact]
    public void Render_MissingRights_ReturnsTemplateMissingFacts()
    {
        var template = new AnsamblulAcordareDrepturiTemplate();
        var facts = HappyPathFacts();
        facts.Remove("rights");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("rights");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["rights"] = new List<string> { "Dreptul la pensie", "Dreptul la indemnizație" },
        ["effectiveFromUtc"] = EffectiveFromUtc,
    };
}
