using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DeciziaPensieTemplate"/> — the Annex 7
/// "Decizia de stabilire a pensiei" Word template.
/// </summary>
public class DeciziaPensieTemplateTests
{
    /// <summary>Reference UTC instant used across the suite.</summary>
    private static readonly DateTime GrantedFromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_HappyPath_ProducesNonEmptyDocxWithMagicBytes()
    {
        var template = new DeciziaPensieTemplate();
        var facts = HappyPathFacts();

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Length.Should().BeGreaterThan(4);

        // ZIP local-file-header signature — DOCX is a ZIP envelope.
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_HappyPath_ProducesStructurallyValidOpenXml()
    {
        var template = new DeciziaPensieTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        paragraphs.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_MissingRequiredFact_ReturnsTemplateMissingFacts()
    {
        var template = new DeciziaPensieTemplate();
        var facts = HappyPathFacts();
        var partial = facts.Where(kvp => kvp.Key != "beneficiaryFullName")
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var result = template.Render(partial);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("beneficiaryFullName");
    }

    [Fact]
    public void Render_FormatsMonthlyAmountAsMdl()
    {
        var template = new DeciziaPensieTemplate();
        var facts = HappyPathFacts();
        // Override the amount to a known value so we can assert the exact formatted output.
        facts["monthlyAmountMdl"] = 1234.5m;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = ExtractAllText(result.Value);
        text.Should().Contain("1,234.50 MDL");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["beneficiaryAddress"] = "mun. Chișinău, str. Pacii 10/2",
        ["serviceCode"] = "SP-PENSIE-LIMITĂ",
        ["serviceTitleRo"] = "Pensia pentru limită de vârstă",
        ["grantedFromUtc"] = GrantedFromUtc,
        ["monthlyAmountMdl"] = 2500.00m,
    };

    /// <summary>Extracts the concatenated body text of a DOCX byte array for assertions.</summary>
    internal static string ExtractAllText(byte[] docxBytes)
    {
        using var ms = new MemoryStream(docxBytes);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        return string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(t => t.Text));
    }
}
