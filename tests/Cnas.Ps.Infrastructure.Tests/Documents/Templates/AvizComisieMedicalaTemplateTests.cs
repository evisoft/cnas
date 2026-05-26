using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="AvizComisieMedicalaTemplate"/> — the Annex 7
/// "Aviz comisie medicală" (medical-commission opinion) template.
/// </summary>
public class AvizComisieMedicalaTemplateTests
{
    /// <summary>Reference UTC date of birth used across the suite.</summary>
    private static readonly DateTime DateOfBirthUtc = new(1970, 3, 14, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new AvizComisieMedicalaTemplate();

        template.TemplateCode.Should().Be(AvizComisieMedicalaTemplate.Code);
        template.TemplateCode.Should().Be("aviz-comisie-medicala");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new AvizComisieMedicalaTemplate();

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
    public void Render_RendersPatientAndDiagnosisAndVerdict()
    {
        var template = new AvizComisieMedicalaTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2000000000007");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("1970-03-14");
        text.Should().Contain("Hipertensiune arterială gradul II");
        text.Should().Contain("Inapt pentru muncă pe termen lung");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new AvizComisieMedicalaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("diagnosis");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("diagnosis");
    }

    [Fact]
    public void Render_MissingVerdict_ReturnsTemplateMissingFacts()
    {
        var template = new AvizComisieMedicalaTemplate();
        var facts = HappyPathFacts();
        facts.Remove("verdict");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("verdict");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["patientIdnp"] = "2000000000007",
        ["patientFullName"] = "Ion Popescu",
        ["dateOfBirthUtc"] = DateOfBirthUtc,
        ["diagnosis"] = "Hipertensiune arterială gradul II, complicații cardiovasculare.",
        ["verdict"] = "Inapt pentru muncă pe termen lung; se recomandă reabilitare medicală.",
    };
}
