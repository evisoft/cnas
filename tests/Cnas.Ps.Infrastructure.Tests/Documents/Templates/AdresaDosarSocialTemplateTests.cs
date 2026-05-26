using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdresaDosarSocialTemplate"/> — the Annex 7
/// "Adresă privind dosarul social" (social-dossier transfer cover letter) template.
/// </summary>
/// <remarks>
/// This is an administrative cover letter used when transferring a social dossier between
/// CTAS branches (e.g. beneficiary changes residency from Chișinău to Bălți).
/// </remarks>
public class AdresaDosarSocialTemplateTests
{
    /// <summary>Reference UTC instant used across the suite.</summary>
    private static readonly DateTime TransferUtc = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdresaDosarSocialTemplate();

        template.TemplateCode.Should().Be(AdresaDosarSocialTemplate.Code);
        template.TemplateCode.Should().Be("adresa-dosar-social");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdresaDosarSocialTemplate();

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
    public void Render_ContainsBranchNamesAndDossierSqid()
    {
        var template = new AdresaDosarSocialTemplate();
        var facts = HappyPathFacts();
        facts["sourceBranch"] = "CTAS Chișinău";
        facts["destinationBranch"] = "CTAS Bălți";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("CTAS Chișinău");
        text.Should().Contain("CTAS Bălți");
        text.Should().Contain("SQID-DOSS-456");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("2026-03-10");
    }

    [Fact]
    public void Render_MissingDestinationBranch_ReturnsTemplateMissingFacts()
    {
        var template = new AdresaDosarSocialTemplate();
        var facts = HappyPathFacts();
        facts.Remove("destinationBranch");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("destinationBranch");
    }

    [Fact]
    public void Render_WithoutOptionalTransferReason_StillSucceeds()
    {
        // transferReason is optional — when omitted, the template must still render
        // without throwing.
        var template = new AdresaDosarSocialTemplate();
        var facts = HappyPathFacts();
        facts.Remove("transferReason");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("SQID-DOSS-456");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["dossierSqid"] = "SQID-DOSS-456",
        ["sourceBranch"] = "CTAS Chișinău",
        ["destinationBranch"] = "CTAS Bălți",
        ["transferUtc"] = TransferUtc,
        ["transferReason"] = "Schimbarea domiciliului beneficiarului.",
    };
}
