using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="NotificarePlataTransferataTemplate"/> — the Annex 7
/// "Notificare privind plata transferată" (payment-method transfer notification) template.
/// </summary>
public class NotificarePlataTransferataTemplateTests
{
    /// <summary>Reference UTC effective-from date used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new NotificarePlataTransferataTemplate();

        template.TemplateCode.Should().Be(NotificarePlataTransferataTemplate.Code);
        template.TemplateCode.Should().Be("notificare-plata-transferata");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new NotificarePlataTransferataTemplate();

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
    public void Render_ContainsPreviousAndNewMethodAndDate()
    {
        var template = new NotificarePlataTransferataTemplate();
        var facts = HappyPathFacts();
        facts["previousMethod"] = "ridicare numerar la oficiul poștal";
        facts["newMethod"] = "transfer bancar Banca de Economii — cont curent";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("ridicare numerar la oficiul poștal");
        text.Should().Contain("transfer bancar Banca de Economii");
        text.Should().Contain("2026-09-01");
        text.Should().Contain("Andrei Lupu");
        text.Should().Contain("NOTIFICARE — PLATĂ TRANSFERATĂ");
    }

    [Fact]
    public void Render_MissingNewMethod_ReturnsTemplateMissingFacts()
    {
        var template = new NotificarePlataTransferataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("newMethod");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("newMethod");
    }

    [Fact]
    public void Render_WithoutOptionalTransferReason_StillSucceeds()
    {
        // transferReason is optional — render must succeed without it.
        var template = new NotificarePlataTransferataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("transferReason");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Andrei Lupu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000020",
        ["beneficiaryFullName"] = "Andrei Lupu",
        ["dossierSqid"] = "SQID-DOSS-NPT",
        ["previousMethod"] = "metoda veche",
        ["newMethod"] = "metoda nouă",
        ["effectiveFromUtc"] = EffectiveFromUtc,
        ["transferReason"] = "La cererea beneficiarului, înregistrată sub nr. 12345/2026.",
    };
}
