using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="ConfirmareSumaTemplate"/> — the Annex 7
/// "Confirmarea sumei" (MDL amount confirmation) template.
/// </summary>
public class ConfirmareSumaTemplateTests
{
    /// <summary>Reference UTC period-start instant used across the suite.</summary>
    private static readonly DateTime FromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reference UTC period-end instant used across the suite.</summary>
    private static readonly DateTime ToUtc = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new ConfirmareSumaTemplate();

        template.TemplateCode.Should().Be(ConfirmareSumaTemplate.Code);
        template.TemplateCode.Should().Be("confirmare-suma");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new ConfirmareSumaTemplate();

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
    public void Render_MoneyFormattedCorrectly()
    {
        var template = new ConfirmareSumaTemplate();
        var facts = HappyPathFacts();
        // Override the amount to a known value so we can assert the exact formatted output.
        facts["amountMdl"] = 12345.67m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("12,345.67 MDL");
        // Period dates rendered.
        text.Should().Contain("2026-01-01");
        text.Should().Contain("2026-12-31");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new ConfirmareSumaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("beneficiaryIdnp");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("beneficiaryIdnp");
    }

    [Fact]
    public void Render_MissingAmount_ReturnsTemplateMissingFacts()
    {
        var template = new ConfirmareSumaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("amountMdl");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("amountMdl");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["amountMdl"] = 1000.00m,
        ["fromUtc"] = FromUtc,
        ["toUtc"] = ToUtc,
    };
}
