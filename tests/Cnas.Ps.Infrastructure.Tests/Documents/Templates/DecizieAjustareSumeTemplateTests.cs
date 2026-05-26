using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DecizieAjustareSumeTemplate"/> — the Annex 7
/// "Decizie de ajustare a sumei" (already-paid-amount correction) template.
/// </summary>
public class DecizieAjustareSumeTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DecizieAjustareSumeTemplate();

        template.TemplateCode.Should().Be(DecizieAjustareSumeTemplate.Code);
        template.TemplateCode.Should().Be("decizie-ajustare-sume");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DecizieAjustareSumeTemplate();

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
    public void Render_ContainsAmountsAndDelta()
    {
        var template = new DecizieAjustareSumeTemplate();
        var facts = HappyPathFacts();
        facts["originalAmountMdl"] = 1000.00m;
        facts["correctedAmountMdl"] = 1234.50m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("1,000.00 MDL");
        text.Should().Contain("1,234.50 MDL");
        // delta = +234.50 MDL
        text.Should().Contain("+234.50 MDL");
        text.Should().Contain("Grigore Cojocaru");
        text.Should().Contain("DECIZIE DE AJUSTARE A SUMEI");
    }

    [Fact]
    public void Render_NegativeDelta_FormattedWithMinusSign()
    {
        // When the corrected amount is lower than the original, the beneficiary owes a
        // return — the delta line must be rendered with an explicit "-" sign.
        var template = new DecizieAjustareSumeTemplate();
        var facts = HappyPathFacts();
        facts["originalAmountMdl"] = 1500.00m;
        facts["correctedAmountMdl"] = 1300.00m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("-200.00 MDL");
    }

    [Fact]
    public void Render_MissingCorrectedAmount_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieAjustareSumeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("correctedAmountMdl");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("correctedAmountMdl");
    }

    [Fact]
    public void Render_WithoutOptionalReferencePaymentSqid_StillSucceeds()
    {
        // referencePaymentSqid is optional — render must succeed without it.
        var template = new DecizieAjustareSumeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("referencePaymentSqid");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Grigore Cojocaru");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000021",
        ["beneficiaryFullName"] = "Grigore Cojocaru",
        ["dossierSqid"] = "SQID-DOSS-DAS",
        ["correctionReason"] = "Eroare de calcul depistată la verificarea încrucișată.",
        ["originalAmountMdl"] = 800.00m,
        ["correctedAmountMdl"] = 850.00m,
        ["referencePaymentSqid"] = "SQID-PAY-9001",
    };
}
