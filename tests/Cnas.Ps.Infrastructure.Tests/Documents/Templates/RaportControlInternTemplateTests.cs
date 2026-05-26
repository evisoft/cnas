using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="RaportControlInternTemplate"/> — the Annex 7
/// "Raport de control intern" (internal-audit report) template.
/// </summary>
public class RaportControlInternTemplateTests
{
    /// <summary>Reference UTC audit-period start used across the suite.</summary>
    private static readonly DateTime FromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reference UTC audit-period end used across the suite.</summary>
    private static readonly DateTime ToUtc = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new RaportControlInternTemplate();

        template.TemplateCode.Should().Be(RaportControlInternTemplate.Code);
        template.TemplateCode.Should().Be("raport-control-intern");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new RaportControlInternTemplate();

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
    public void Render_RendersFindingsTableAndRecommendations()
    {
        var template = new RaportControlInternTemplate();
        var findings = new List<RaportControlInternTemplate.Finding>
        {
            new(Title: "Lipsă semnătură electronică pe 3 decizii", Severity: "Medie"),
            new(Title: "Întârziere la procesare > 30 zile", Severity: "Ridicată"),
        };
        var recs = new List<string>
        {
            "Implementarea verificării automate a semnăturii electronice.",
            "Revizuirea SLA-urilor interne pentru procesarea cererilor.",
        };
        var facts = HappyPathFacts();
        facts["findings"] = findings;
        facts["recommendations"] = recs;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var tables = doc.MainDocumentPart!.Document.Body!.Descendants<Table>().ToList();
        tables.Should().NotBeEmpty();

        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Maria Ionescu");
        // Period as UTC dates.
        text.Should().Contain("2026-01-01");
        text.Should().Contain("2026-03-31");
        foreach (var f in findings)
        {
            text.Should().Contain(f.Title);
            text.Should().Contain(f.Severity);
        }
        foreach (var r in recs)
        {
            text.Should().Contain(r);
        }
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new RaportControlInternTemplate();
        var facts = HappyPathFacts();
        facts.Remove("auditorFullName");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("auditorFullName");
    }

    [Fact]
    public void Render_MissingRecommendations_ReturnsTemplateMissingFacts()
    {
        var template = new RaportControlInternTemplate();
        var facts = HappyPathFacts();
        facts.Remove("recommendations");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("recommendations");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["fromUtc"] = FromUtc,
        ["toUtc"] = ToUtc,
        ["auditorFullName"] = "Maria Ionescu",
        ["findings"] = new List<RaportControlInternTemplate.Finding>
        {
            new(Title: "Test finding", Severity: "Joasă"),
        },
        ["recommendations"] = new List<string> { "Test recommendation" },
    };
}
