using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AdeverintaCotizareTemplate"/> — the Annex 7
/// "Adeverință de cotizare" (contribution certificate) template.
/// </summary>
public class AdeverintaCotizareTemplateTests
{
    /// <summary>Reference UTC period-start used across the suite.</summary>
    private static readonly DateTime FromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reference UTC period-end used across the suite.</summary>
    private static readonly DateTime ToUtc = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AdeverintaCotizareTemplate();

        template.TemplateCode.Should().Be(AdeverintaCotizareTemplate.Code);
        template.TemplateCode.Should().Be("adeverinta-cotizare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AdeverintaCotizareTemplate();

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
        // Monthly contributions table must be present.
        var tables = doc.MainDocumentPart!.Document.Body!.Descendants<Table>().ToList();
        tables.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AdeverintaCotizareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("beneficiaryIdnp");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("beneficiaryIdnp");
    }

    [Fact]
    public void Render_MissingMonthlyContributions_ReturnsTemplateMissingFacts()
    {
        var template = new AdeverintaCotizareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("monthlyContributions");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("monthlyContributions");
    }

    [Fact]
    public void Render_MoneyFormattedCorrectly()
    {
        var template = new AdeverintaCotizareTemplate();
        var contributions = new List<AdeverintaCotizareTemplate.MonthlyContribution>
        {
            new(Month: "2026-01", AmountMdl: 1000.00m),
            new(Month: "2026-02", AmountMdl: 1234.50m),
            new(Month: "2026-03", AmountMdl: 1500.00m),
        };
        var facts = HappyPathFacts();
        facts["monthlyContributions"] = contributions;
        facts["totalAmountMdl"] = 3734.50m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("1,000.00 MDL");
        text.Should().Contain("1,234.50 MDL");
        text.Should().Contain("1,500.00 MDL");
        text.Should().Contain("3,734.50 MDL");
        text.Should().Contain("2026-01");
        text.Should().Contain("2026-02");
        text.Should().Contain("2026-03");
        // Period UTC dates rendered.
        text.Should().Contain("2026-01-01");
        text.Should().Contain("2026-03-31");
        text.Should().Contain("Ion Popescu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["fromUtc"] = FromUtc,
        ["toUtc"] = ToUtc,
        ["monthlyContributions"] = new List<AdeverintaCotizareTemplate.MonthlyContribution>
        {
            new(Month: "2026-01", AmountMdl: 1000.00m),
        },
        ["totalAmountMdl"] = 1000.00m,
    };
}
