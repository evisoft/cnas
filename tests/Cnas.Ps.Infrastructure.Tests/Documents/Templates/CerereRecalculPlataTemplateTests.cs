using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="CerereRecalculPlataTemplate"/> — the Annex 7
/// "Cerere de recalcul a plății" (payment-recalculation request acknowledgment) template.
/// </summary>
/// <remarks>
/// Distinct from the existing <c>DispozitieRecalculTemplate</c>: the dispoziție carries
/// the final recalculation outcome (before/after amounts); this template is the formal
/// acknowledgment that the beneficiary's recalculation request has been received and
/// queued for examination — including the expected response-by deadline.
/// </remarks>
public class CerereRecalculPlataTemplateTests
{
    /// <summary>Reference UTC instant for the request-received timestamp.</summary>
    private static readonly DateTime RequestUtc = new(2026, 2, 1, 10, 15, 0, DateTimeKind.Utc);

    /// <summary>Reference UTC instant for the response-by deadline.</summary>
    private static readonly DateTime ResponseByUtc = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new CerereRecalculPlataTemplate();

        template.TemplateCode.Should().Be(CerereRecalculPlataTemplate.Code);
        template.TemplateCode.Should().Be("cerere-recalcul-plata");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new CerereRecalculPlataTemplate();

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
    public void Render_ContainsRequestReasonAndResponseDate()
    {
        var template = new CerereRecalculPlataTemplate();
        var facts = HappyPathFacts();
        facts["requestReason"] = "Includerea perioadei de stagiu nedovedit anterior.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Includerea perioadei de stagiu nedovedit");
        text.Should().Contain("2026-02-01");
        text.Should().Contain("2026-03-01");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("SQID-DOSS-321");
    }

    [Fact]
    public void Render_MissingRequestReason_ReturnsTemplateMissingFacts()
    {
        var template = new CerereRecalculPlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("requestReason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("requestReason");
    }

    [Fact]
    public void Render_WithoutOptionalAttachmentsList_StillSucceeds()
    {
        // attachmentsList is optional — when omitted, the attachments section is skipped.
        var template = new CerereRecalculPlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("attachmentsList");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Ion Popescu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["dossierSqid"] = "SQID-DOSS-321",
        ["requestReason"] = "Motiv recalcul",
        ["requestUtc"] = RequestUtc,
        ["responseByUtc"] = ResponseByUtc,
        ["attachmentsList"] = "Adeverință de salariu pentru anii 2010-2015; Carnet de muncă (copie).",
    };
}
