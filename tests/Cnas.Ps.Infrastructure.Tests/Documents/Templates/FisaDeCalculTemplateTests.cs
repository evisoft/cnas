using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="FisaDeCalculTemplate"/> — the Annex 7 "Fișa de calcul" template.
/// </summary>
public class FisaDeCalculTemplateTests
{
    [Fact]
    public void Render_HappyPath_ProducesDocxWithMagicBytes()
    {
        var template = new FisaDeCalculTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_HappyPath_ContainsCalculationFactsAsTable()
    {
        var template = new FisaDeCalculTemplate();
        var calcFacts = new Dictionary<string, string>
        {
            ["Vechime în muncă"] = "30 ani",
            ["Coeficient"] = "1.25",
            ["Salariu mediu"] = "8000.00 MDL",
        };
        var facts = HappyPathFacts();
        facts["calculationFacts"] = calcFacts;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var tables = doc.MainDocumentPart!.Document.Body!.Descendants<Table>().ToList();
        tables.Should().NotBeEmpty();

        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Vechime în muncă");
        text.Should().Contain("30 ani");
        text.Should().Contain("Salariu mediu");
    }

    [Fact]
    public void Render_MissingBeneficiaryIdnp_ReturnsTemplateMissingFacts()
    {
        var template = new FisaDeCalculTemplate();
        var facts = HappyPathFacts();
        facts.Remove("beneficiaryIdnp");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
    }

    [Fact]
    public void Render_RendersTotalAmountInBold()
    {
        var template = new FisaDeCalculTemplate();
        var facts = HappyPathFacts();
        facts["totalAmountMdl"] = 4321.00m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("4,321.00 MDL");
    }

    /// <summary>
    /// Deterministic footer: supplying <c>generatedAtUtc</c> in the facts must drive
    /// the "Generat automat la …" footer so the rendered DOCX is byte-stable across
    /// runs. This is the test-side surface for the upcoming
    /// <c>ICnasTimeProvider</c> migration — golden snapshots depend on it.
    /// </summary>
    [Fact]
    public void Render_WithGeneratedAtUtcFact_ProducesDeterministicFooter()
    {
        var template = new FisaDeCalculTemplate();
        var facts = HappyPathFacts();
        // Pinned instant — anything fixed works; we just need the test to be stable.
        facts["generatedAtUtc"] = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        // The footer format is "yyyy-MM-dd HH:mm UTC" (see DocxRenderHelpers.UtcFormat).
        text.Should().Contain("2026-01-15 08:30 UTC");
        text.Should().Contain("Generat automat la 2026-01-15 08:30 UTC");
    }

    /// <summary>
    /// Absent-fact contract: when <c>generatedAtUtc</c> is not supplied, the template
    /// renders without the UTC footer rather than leaking <see cref="DateTime.UtcNow"/>
    /// from inside the renderer. The caller (<c>DocumentGenerationService</c>, wired
    /// with <c>ICnasTimeProvider</c>) is responsible for supplying the timestamp.
    /// </summary>
    [Fact]
    public void Render_WithoutGeneratedAtUtcFact_OmitsTheFooter()
    {
        var template = new FisaDeCalculTemplate();
        var facts = HappyPathFacts();
        facts.Should().NotContainKey("generatedAtUtc");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().NotContain("Generat automat la");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["serviceCode"] = "SP-FISA",
        ["calculationFacts"] = new Dictionary<string, string>
        {
            ["Vechime"] = "30 ani",
            ["Coeficient"] = "1.25",
        },
        ["totalAmountMdl"] = 2500.00m,
    };
}
