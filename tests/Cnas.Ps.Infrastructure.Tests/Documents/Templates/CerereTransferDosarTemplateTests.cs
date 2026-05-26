using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="CerereTransferDosarTemplate"/> — the Annex 7
/// "Cerere de transfer al dosarului" (citizen-facing inter-branch dossier
/// transfer request) template.
/// </summary>
public class CerereTransferDosarTemplateTests
{
    /// <summary>Reference request UTC instant used across the suite.</summary>
    private static readonly DateTime RequestedOnUtc = new(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new CerereTransferDosarTemplate();

        template.TemplateCode.Should().Be(CerereTransferDosarTemplate.Code);
        template.TemplateCode.Should().Be("cerere-transfer-dosar");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new CerereTransferDosarTemplate();

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
        var template = new CerereTransferDosarTemplate();
        var facts = HappyPathFacts();
        facts["beneficiaryFullName"] = "Elena Lupu";
        facts["beneficiaryIdnp"] = "2001002003004";
        facts["dossierSqid"] = "dsr_q7Rt8";
        facts["sourceBranch"] = "Direcția Chișinău — Botanica";
        facts["destinationBranch"] = "Direcția Cahul";
        facts["transferReason"] = "Schimbarea domiciliului permanent în raionul Cahul.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("CERERE DE TRANSFER AL DOSARULUI");
        text.Should().Contain("Elena Lupu");
        text.Should().Contain("2001002003004");
        text.Should().Contain("dsr_q7Rt8");
        text.Should().Contain("Direcția Chișinău — Botanica");
        text.Should().Contain("Direcția Cahul");
        text.Should().Contain("Schimbarea domiciliului permanent");
        text.Should().Contain("2026-05-12");
    }

    [Fact]
    public void Render_MissingDestinationBranch_ReturnsTemplateMissingFacts()
    {
        var template = new CerereTransferDosarTemplate();
        var facts = HappyPathFacts();
        facts.Remove("destinationBranch");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("destinationBranch");
    }

    [Fact]
    public void Render_WithoutOptionalContactPhone_StillSucceeds()
    {
        // contactPhone is optional — render must succeed without it.
        var template = new CerereTransferDosarTemplate();
        var facts = HappyPathFacts();
        facts.Remove("contactPhone");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Elena Lupu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Elena Lupu",
        ["dossierSqid"] = "dsr_p1q2r",
        ["sourceBranch"] = "Direcția Chișinău",
        ["destinationBranch"] = "Direcția Bălți",
        ["transferReason"] = "Schimbarea domiciliului permanent.",
        ["requestedOnUtc"] = RequestedOnUtc,
        ["contactPhone"] = "+373 60 000 000",
    };
}
