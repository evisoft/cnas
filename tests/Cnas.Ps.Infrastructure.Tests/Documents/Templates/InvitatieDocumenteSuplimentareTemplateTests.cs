using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="InvitatieDocumenteSuplimentareTemplate"/> — Annex 7
/// "Invitație pentru documente suplimentare" template.
/// </summary>
public class InvitatieDocumenteSuplimentareTemplateTests
{
    private static readonly DateTime DeadlineUtc = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_HappyPath_ProducesDocxWithMagicBytes()
    {
        var template = new InvitatieDocumenteSuplimentareTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_RendersGreetingAndRequestedDocuments()
    {
        var template = new InvitatieDocumenteSuplimentareTemplate();
        var docs = new List<string>
        {
            "Copia buletinului de identitate",
            "Adeverința de la locul de muncă",
            "Extras din cartea de muncă",
        };
        var facts = HappyPathFacts();
        facts["requestedDocuments"] = docs;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        // Salutation must mention the beneficiary's name.
        text.Should().Contain("Ion Popescu");
        foreach (var d in docs)
        {
            text.Should().Contain(d);
        }
        // Deadline must be rendered as UTC.
        text.Should().Contain("2026-06-30");
    }

    [Fact]
    public void Render_MissingRequestedDocuments_ReturnsTemplateMissingFacts()
    {
        var template = new InvitatieDocumenteSuplimentareTemplate();
        var facts = HappyPathFacts();
        facts.Remove("requestedDocuments");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryFullName"] = "Ion Popescu",
        ["dossierSqid"] = "SQID-DOSS-42",
        ["requestedDocuments"] = new List<string> { "Doc A", "Doc B" },
        ["deadlineUtc"] = DeadlineUtc,
    };
}
