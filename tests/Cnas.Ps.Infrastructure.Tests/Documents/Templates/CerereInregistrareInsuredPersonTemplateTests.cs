using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="CerereInregistrareInsuredPersonTemplate"/> —
/// the Annex 7 insured-person registration cerere template (R2000 §8.7.1).
/// </summary>
public sealed class CerereInregistrareInsuredPersonTemplateTests
{
    private static readonly DateTime InsuredDob = new(1990, 5, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new CerereInregistrareInsuredPersonTemplate();

        template.TemplateCode.Should().Be(CerereInregistrareInsuredPersonTemplate.Code);
        template.TemplateCode.Should().Be("cerere-inregistrare-insured-person");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new CerereInregistrareInsuredPersonTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Length.Should().BeGreaterThan(4);

        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().Should().NotBeEmpty();
    }

    [Fact]
    public void Render_ContainsIdentityBlocks()
    {
        var template = new CerereInregistrareInsuredPersonTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value!);
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("Maria Popescu");
        text.Should().Contain("2000000000007");
        text.Should().Contain("2000000000008");
        text.Should().Contain("1990-05-12");
        text.Should().Contain("Soție");
    }

    [Fact]
    public void Render_MissingInsuredIdnp_ReturnsTemplateMissingFacts()
    {
        var template = new CerereInregistrareInsuredPersonTemplate();
        var facts = HappyPathFacts();
        facts.Remove("insuredIdnp");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("insuredIdnp");
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["solicitantFullName"] = "Ion Popescu",
        ["solicitantIdnp"] = "2000000000007",
        ["insuredFullName"] = "Maria Popescu",
        ["insuredIdnp"] = "2000000000008",
        ["insuredDobUtc"] = InsuredDob,
        ["relationship"] = "Soție",
    };
}
