using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="RaportStatisticaPlatiTemplate"/> — the Annex 7
/// statistical-payments report template (R2002 §8.7.3.2).
/// </summary>
public sealed class RaportStatisticaPlatiTemplateTests
{
    private static readonly DateTime FromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new RaportStatisticaPlatiTemplate();

        template.TemplateCode.Should().Be(RaportStatisticaPlatiTemplate.Code);
        template.TemplateCode.Should().Be("raport-statistica-plati");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new RaportStatisticaPlatiTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Length.Should().BeGreaterThan(4);
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
    public void Render_BodyContainsTotalAndCategoryRows()
    {
        var template = new RaportStatisticaPlatiTemplate();
        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value!);
        text.Should().Contain("Pensii limită de vârstă");
        text.Should().Contain("Pensii invaliditate");
        text.Should().Contain("100000.00", "the categories total to 100 000.00 MDL.");
        text.Should().Contain("TOTAL");
    }

    [Fact]
    public void Render_MissingRows_ReturnsTemplateMissingFacts()
    {
        var template = new RaportStatisticaPlatiTemplate();
        var facts = HappyPathFacts();
        facts.Remove("rows");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("rows");
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["fromUtc"] = FromUtc,
        ["toUtc"] = ToUtc,
        ["rows"] = new List<RaportStatisticaPlatiTemplate.PaymentRow>
        {
            new("Pensii limită de vârstă", 75000.00m),
            new("Pensii invaliditate", 25000.00m),
        },
    };
}
