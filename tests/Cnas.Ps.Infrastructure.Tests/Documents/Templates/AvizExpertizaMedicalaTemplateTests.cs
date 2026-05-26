using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AvizExpertizaMedicalaTemplate"/> — the Annex 7
/// "Aviz privind expertiza medicală" (medical-expertise opinion) template.
/// </summary>
/// <remarks>
/// Distinct from the existing <c>AvizComisieMedicalaTemplate</c>: the commission template
/// captures the verdict of the medical commission; this template captures the opinion of
/// an individual expert evaluator forwarded to that commission.
/// </remarks>
public class AvizExpertizaMedicalaTemplateTests
{
    /// <summary>Reference UTC instant used across the suite.</summary>
    private static readonly DateTime EvaluationUtc = new(2026, 4, 15, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AvizExpertizaMedicalaTemplate();

        template.TemplateCode.Should().Be(AvizExpertizaMedicalaTemplate.Code);
        template.TemplateCode.Should().Be("aviz-expertiza-medicala");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AvizExpertizaMedicalaTemplate();

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
    public void Render_ContainsExpertNameAndConclusion()
    {
        var template = new AvizExpertizaMedicalaTemplate();
        var facts = HappyPathFacts();
        facts["expertConclusion"] = "Capacitatea de muncă este redusă cu 60% conform criteriilor în vigoare.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Capacitatea de muncă este redusă cu 60%");
        text.Should().Contain("Dr. Maria Ionescu");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("2026-04-15");
    }

    [Fact]
    public void Render_MissingExpertFullName_ReturnsTemplateMissingFacts()
    {
        var template = new AvizExpertizaMedicalaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("expertFullName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("expertFullName");
    }

    [Fact]
    public void Render_WithoutOptionalRecommendation_StillSucceeds()
    {
        // recommendation is optional — when omitted, the template must still render.
        var template = new AvizExpertizaMedicalaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("recommendation");

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
        ["expertFullName"] = "Dr. Maria Ionescu",
        ["expertSpecialty"] = "Medicină internă",
        ["evaluationUtc"] = EvaluationUtc,
        ["expertConclusion"] = "Concluzia expertului",
        ["recommendation"] = "Reexaminare după 12 luni.",
    };
}
