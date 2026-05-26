using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaArhivareTemplate"/> — the Annex 7
/// "Adresă privind arhivarea dosarului" template (notice of dossier
/// archival closure).
/// </summary>
public class AdresaArhivareTemplateTests
{
    /// <summary>Reference archival UTC instant used across the suite.</summary>
    private static readonly DateTime ArchivedOnUtc = new(2026, 3, 31, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaArhivareTemplate();

        template.TemplateCode.Should().Be(AdresaArhivareTemplate.Code);
        template.TemplateCode.Should().Be("adresa-arhivare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaArhivareTemplate();

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
    public void Render_SubstitutesPlaceholdersIntoBody()
    {
        var template = new AdresaArhivareTemplate();
        var facts = HappyPathFacts();
        facts["recipientFullName"] = "Domnului Petre Curatorul";
        facts["dossierSqid"] = "dsr_arh_001";
        facts["archivalReason"] = "Închiderea dosarului ca urmare a decesului beneficiarului.";
        facts["archiveLocation"] = "Arhiva CNAS — Depozit central, raft 12, dosarul 4521.";
        facts["retentionYears"] = 75;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("ADRESĂ PRIVIND ARHIVAREA DOSARULUI");
        text.Should().Contain("Domnului Petre Curatorul");
        text.Should().Contain("dsr_arh_001");
        text.Should().Contain("Închiderea dosarului ca urmare a decesului");
        text.Should().Contain("Arhiva CNAS — Depozit central");
        text.Should().Contain("75");
        text.Should().Contain("2026-03-31");
    }

    [Fact]
    public void Render_MissingArchivalReason_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaArhivareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("archivalReason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("archivalReason");
    }

    [Fact]
    public void Render_WithoutOptionalRetentionYears_StillSucceeds()
    {
        // retentionYears is optional — render must succeed without it.
        var template = new AdresaArhivareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("retentionYears");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Domnului Curatorul");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["recipientFullName"] = "Domnului Curatorul",
        ["dossierSqid"] = "dsr_arh_zzz",
        ["archivalReason"] = "Închiderea administrativă a dosarului.",
        ["archiveLocation"] = "Arhiva CNAS — Depozit central.",
        ["archivedOnUtc"] = ArchivedOnUtc,
        ["retentionYears"] = 50,
    };
}
