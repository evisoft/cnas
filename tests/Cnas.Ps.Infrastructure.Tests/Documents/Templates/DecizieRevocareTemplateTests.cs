using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DecizieRevocareTemplate"/> — the Annex 7
/// "Decizie de revocare" (revocation decision) template.
/// </summary>
public class DecizieRevocareTemplateTests
{
    /// <summary>Reference UTC effective-from used across the suite.</summary>
    private static readonly DateTime EffectiveFromUtc = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DecizieRevocareTemplate();

        template.TemplateCode.Should().Be(DecizieRevocareTemplate.Code);
        template.TemplateCode.Should().Be("decizie-revocare");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DecizieRevocareTemplate();

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
    public void Render_RendersRecipientReasonAndEffectiveFrom()
    {
        var template = new DecizieRevocareTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("2000000000007");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("Decesul beneficiarului confirmat prin certificat oficial.");
        // Effective-from UTC date.
        text.Should().Contain("2026-05-01");
    }

    [Fact]
    public void Render_MissingFact_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieRevocareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("reason");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("reason");
    }

    [Fact]
    public void Render_MissingEffectiveFromUtc_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieRevocareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("effectiveFromUtc");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("effectiveFromUtc");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["reason"] = "Decesul beneficiarului confirmat prin certificat oficial.",
        ["effectiveFromUtc"] = EffectiveFromUtc,
    };
}
