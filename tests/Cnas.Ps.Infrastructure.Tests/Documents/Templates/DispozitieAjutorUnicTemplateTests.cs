using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DispozitieAjutorUnicTemplate"/> — the Annex 7
/// "Dispoziție privind acordarea ajutorului unic" (one-time aid disposition) template.
/// </summary>
public class DispozitieAjutorUnicTemplateTests
{
    /// <summary>Reference UTC disbursement date used across the suite.</summary>
    private static readonly DateTime DisbursementUtc = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DispozitieAjutorUnicTemplate();

        template.TemplateCode.Should().Be(DispozitieAjutorUnicTemplate.Code);
        template.TemplateCode.Should().Be("dispozitie-ajutor-unic");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DispozitieAjutorUnicTemplate();

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
    public void Render_ContainsAmountAndDate()
    {
        var template = new DispozitieAjutorUnicTemplate();
        var facts = HappyPathFacts();
        facts["aidAmountMdl"] = 5500.75m;
        facts["legalGround"] = "Hotărârea Guvernului nr. 123/2026 privind ajutorul unic.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("5,500.75 MDL");
        text.Should().Contain("Hotărârea Guvernului nr. 123/2026");
        text.Should().Contain("2026-06-30");
        text.Should().Contain("Elena Vasilescu");
        text.Should().Contain("DISPOZIȚIE — ACORDARE AJUTOR UNIC");
    }

    [Fact]
    public void Render_MissingAidAmount_ReturnsTemplateMissingFacts()
    {
        var template = new DispozitieAjutorUnicTemplate();
        var facts = HappyPathFacts();
        facts.Remove("aidAmountMdl");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("aidAmountMdl");
    }

    [Fact]
    public void Render_WithoutOptionalPaymentMethod_StillSucceeds()
    {
        // paymentMethod is optional — render must succeed without it.
        var template = new DispozitieAjutorUnicTemplate();
        var facts = HappyPathFacts();
        facts.Remove("paymentMethod");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Elena Vasilescu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000019",
        ["beneficiaryFullName"] = "Elena Vasilescu",
        ["dossierSqid"] = "SQID-DOSS-DAU",
        ["legalGround"] = "Temei legal",
        ["aidAmountMdl"] = 1000.00m,
        ["disbursementUtc"] = DisbursementUtc,
        ["paymentMethod"] = "transfer bancar pe contul indicat în cerere",
    };
}
