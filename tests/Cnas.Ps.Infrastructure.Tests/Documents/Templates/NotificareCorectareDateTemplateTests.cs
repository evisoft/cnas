using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="NotificareCorectareDateTemplate"/> — the Annex 7
/// "Notificare privind corectarea datelor cu caracter personal" template.
/// </summary>
public class NotificareCorectareDateTemplateTests
{
    /// <summary>Reference correction-applied UTC instant used across the suite.</summary>
    private static readonly DateTime CorrectionAppliedUtc = new(2026, 4, 15, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new NotificareCorectareDateTemplate();

        template.TemplateCode.Should().Be(NotificareCorectareDateTemplate.Code);
        template.TemplateCode.Should().Be("notificare-corectare-date");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new NotificareCorectareDateTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(4);
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);

        using var ms = new MemoryStream(result.Value);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        doc.MainDocumentPart.Should().NotBeNull();
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        paragraphs.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_SubstitutesPlaceholdersIntoBody()
    {
        var template = new NotificareCorectareDateTemplate();
        var facts = HappyPathFacts();
        facts["beneficiaryFullName"] = "Vasile Munteanu";
        facts["beneficiaryIdnp"] = "2009008007006";
        facts["correctedField"] = "Adresa de domiciliu";
        facts["previousValue"] = "str. Veche 10, Chișinău";
        facts["newValue"] = "str. Nouă 22 ap. 5, Chișinău";
        facts["correctionSource"] = "Cererea nr. 4521/2026 depusă la ghișeul Direcției Botanica.";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("NOTIFICARE PRIVIND CORECTAREA DATELOR");
        text.Should().Contain("Vasile Munteanu");
        text.Should().Contain("2009008007006");
        text.Should().Contain("Adresa de domiciliu");
        text.Should().Contain("str. Veche 10, Chișinău");
        text.Should().Contain("str. Nouă 22 ap. 5, Chișinău");
        text.Should().Contain("Cererea nr. 4521/2026");
        text.Should().Contain("2026-04-15");
    }

    [Fact]
    public void Render_MissingNewValue_ReturnsTemplateMissingFacts()
    {
        var template = new NotificareCorectareDateTemplate();
        var facts = HappyPathFacts();
        facts.Remove("newValue");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("newValue");
    }

    [Fact]
    public void Render_WithoutOptionalCaseOfficer_StillSucceeds()
    {
        // caseOfficerFullName is optional — render must succeed without it.
        var template = new NotificareCorectareDateTemplate();
        var facts = HappyPathFacts();
        facts.Remove("caseOfficerFullName");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Vasile Munteanu");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Vasile Munteanu",
        ["correctedField"] = "Numele de familie",
        ["previousValue"] = "Munteanu",
        ["newValue"] = "Munteanu-Cebanu",
        ["correctionSource"] = "Sentința civilă nr. 42 din 10.03.2026.",
        ["correctionAppliedUtc"] = CorrectionAppliedUtc,
        ["caseOfficerFullName"] = "Andrei Custodianul",
    };
}
