using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="CerereModificarePensieTemplate"/> — the
/// Annex 7 pension-modification cerere template (R2000 §8.7.1).
/// </summary>
public sealed class CerereModificarePensieTemplateTests
{
    private static readonly DateTime EffectiveFromUtc = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new CerereModificarePensieTemplate();

        template.TemplateCode.Should().Be(CerereModificarePensieTemplate.Code);
        template.TemplateCode.Should().Be("cerere-modificare-pensie");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new CerereModificarePensieTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Length.Should().BeGreaterThan(4);

        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().Should().NotBeEmpty();
    }

    [Fact]
    public void Render_ContainsBeneficiaryAndModificationInfo()
    {
        var template = new CerereModificarePensieTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value!);
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("2000000000007");
        text.Should().Contain("SQID-DOSS-999");
        text.Should().Contain("Schimbare cont bancar");
        text.Should().Contain("MD24AG00000022511");
        text.Should().Contain("2026-03-01");
    }

    [Fact]
    public void Render_MissingDossierSqid_ReturnsTemplateMissingFacts()
    {
        var template = new CerereModificarePensieTemplate();
        var facts = HappyPathFacts();
        facts.Remove("dossierSqid");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("dossierSqid");
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryFullName"] = "Ion Popescu",
        ["beneficiaryIdnp"] = "2000000000007",
        ["dossierSqid"] = "SQID-DOSS-999",
        ["modificationType"] = "Schimbare cont bancar",
        ["modificationDetails"] = "Solicit transferul plății pensiei pe contul MD24AG00000022511.",
        ["effectiveFromUtc"] = EffectiveFromUtc,
    };
}
