using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DecizieSuspendarePlataTemplate"/> — the Annex 7
/// "Decizie de suspendare a plății" (payment-suspension decision) template.
/// </summary>
/// <remarks>
/// Tests follow the established suite pattern: ZIP-magic happy-path, structural
/// OpenXML smoke, placeholder substitution via the shared <see cref="DeciziaPensieTemplateTests.ExtractAllText"/>
/// helper, missing-required-fact path, and optional-field-omitted resilience.
/// </remarks>
public class DecizieSuspendarePlataTemplateTests
{
    /// <summary>Reference UTC suspension effective-from used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DecizieSuspendarePlataTemplate();

        template.TemplateCode.Should().Be(DecizieSuspendarePlataTemplate.Code);
        template.TemplateCode.Should().Be("decizie-suspendare-plata");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DecizieSuspendarePlataTemplate();

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
    public void Render_ContainsSuspensionReasonAndEffectiveDate()
    {
        var template = new DecizieSuspendarePlataTemplate();
        var facts = HappyPathFacts();
        facts["suspensionReason"] = "Neprezentarea certificatului medical anual.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Neprezentarea certificatului medical anual");
        text.Should().Contain("2026-07-01");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("DECIZIE DE SUSPENDARE A PLĂȚII");
    }

    [Fact]
    public void Render_MissingSuspensionReason_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieSuspendarePlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("suspensionReason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("suspensionReason");
    }

    [Fact]
    public void Render_WithoutOptionalReviewDate_StillSucceeds()
    {
        // The "reviewAfterUtc" key is optional — when omitted, the template must still
        // render successfully (no NullReferenceException), simply skipping the review-date line.
        var template = new DecizieSuspendarePlataTemplate();
        var facts = HappyPathFacts();
        facts.Remove("reviewAfterUtc");

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
        ["dossierSqid"] = "SQID-DOSS-123",
        ["suspensionReason"] = "Motiv suspendare",
        ["effectiveFromUtc"] = EffectiveFromUtc,
        ["reviewAfterUtc"] = EffectiveFromUtc.AddMonths(3),
    };
}
