using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="CalcSporVechimeTemplate"/> — the Annex 7
/// "Calculul sporului de vechime" (seniority bonus calculation) template.
/// </summary>
public class CalcSporVechimeTemplateTests
{
    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new CalcSporVechimeTemplate();

        template.TemplateCode.Should().Be(CalcSporVechimeTemplate.Code);
        template.TemplateCode.Should().Be("calc-spor-vechime");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new CalcSporVechimeTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        // ZIP magic bytes — DOCX is a ZIP envelope.
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
    public void Render_RendersBeneficiaryAndPeriodsAndTotal()
    {
        var template = new CalcSporVechimeTemplate();
        var periods = new List<CalcSporVechimeTemplate.ServicePeriod>
        {
            new(From: "2000-01-01", To: "2010-12-31", Years: 11.00m),
            new(From: "2011-01-01", To: "2020-12-31", Years: 10.00m),
        };
        var facts = HappyPathFacts();
        facts["periods"] = periods;
        facts["totalYears"] = 21.00m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2000000000007");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("2000-01-01");
        text.Should().Contain("2010-12-31");
        text.Should().Contain("2011-01-01");
        text.Should().Contain("2020-12-31");
        text.Should().Contain("21");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new CalcSporVechimeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("totalYears");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("totalYears");
    }

    [Fact]
    public void Render_MissingPeriods_ReturnsTemplateMissingFacts()
    {
        var template = new CalcSporVechimeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("periods");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("periods");
    }

    /// <summary>
    /// Deterministic footer: when the caller supplies <c>generatedAtUtc</c> the rendered
    /// DOCX footer carries that exact timestamp. This is the test-side surface for the
    /// upcoming <c>ICnasTimeProvider</c> migration — golden snapshots depend on it.
    /// </summary>
    [Fact]
    public void Render_WithGeneratedAtUtcFact_ProducesDeterministicFooter()
    {
        var template = new CalcSporVechimeTemplate();
        var facts = HappyPathFacts();
        facts["generatedAtUtc"] = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2026-01-15 08:30 UTC");
        text.Should().Contain("Generat automat la 2026-01-15 08:30 UTC");
    }

    /// <summary>
    /// Absent-fact contract: when <c>generatedAtUtc</c> is missing, the template renders
    /// without the UTC footer rather than leaking <see cref="DateTime.UtcNow"/> from the
    /// renderer. The caller (<c>DocumentGenerationService</c>, wired with
    /// <c>ICnasTimeProvider</c>) supplies the timestamp.
    /// </summary>
    [Fact]
    public void Render_WithoutGeneratedAtUtcFact_OmitsTheFooter()
    {
        var template = new CalcSporVechimeTemplate();
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
        ["periods"] = new List<CalcSporVechimeTemplate.ServicePeriod>
        {
            new(From: "1990-01-01", To: "2020-12-31", Years: 30.00m),
        },
        ["totalYears"] = 30.00m,
    };
}
