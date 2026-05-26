using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DecizieIncetarePlataTemplate"/> — the Annex 7
/// "Decizie de încetare a plății" (final cessation-of-payment decision) template.
/// </summary>
public class DecizieIncetarePlataTemplateTests
{
    /// <summary>Reference cessation-effective UTC instant used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DecizieIncetarePlataTemplate();

        template.TemplateCode.Should().Be(DecizieIncetarePlataTemplate.Code);
        template.TemplateCode.Should().Be("decizie-incetare-plata");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DecizieIncetarePlataTemplate();

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
    public void Render_SubstitutesRequiredFactsIntoBody()
    {
        var template = new DecizieIncetarePlataTemplate();
        var facts = HappyPathFacts();
        facts["beneficiaryFullName"] = "Maria Cioban";
        facts["beneficiaryIdnp"] = "2002003004005";
        facts["dossierSqid"] = "dsr_X9Yk2";
        facts["cessationReason"] = "Deces confirmat al beneficiarului — sursa: actul de stare civilă.";
        facts["legalGround"] = "Art. 35 lit. (a) din Legea privind sistemul public de pensii.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("DECIZIE DE ÎNCETARE A PLĂȚII");
        text.Should().Contain("Maria Cioban");
        text.Should().Contain("2002003004005");
        text.Should().Contain("dsr_X9Yk2");
        text.Should().Contain("Deces confirmat al beneficiarului");
        text.Should().Contain("Art. 35 lit. (a)");
        text.Should().Contain("2026-06-01");
    }

    [Fact]
    public void Render_MissingCessationReason_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieIncetarePlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("cessationReason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("cessationReason");
    }

    [Fact]
    public void Render_WithoutOptionalFinalDisbursementDate_StillSucceeds()
    {
        // finalDisbursementDateUtc is optional — render must succeed without it.
        var template = new DecizieIncetarePlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("finalDisbursementDateUtc");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Ion Beneficiarul");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Beneficiarul",
        ["dossierSqid"] = "dsr_a1b2c",
        ["cessationReason"] = "Pierderea dreptului la prestație conform legii.",
        ["legalGround"] = "Legea nr. 156-XIV din 14 octombrie 1998.",
        ["effectiveFromUtc"] = EffectiveFromUtc,
        ["finalDisbursementDateUtc"] = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
    };
}
