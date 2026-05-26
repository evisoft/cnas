using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DecizieRecuperareSumeTemplate"/> — the Annex 7
/// "Decizie de recuperare a sumelor plătite necuvenit" (over-payment
/// recovery decision) template.
/// </summary>
public class DecizieRecuperareSumeTemplateTests
{
    /// <summary>Reference repayment-deadline UTC instant used across the suite.</summary>
    private static readonly DateTime RepaymentDeadlineUtc = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new DecizieRecuperareSumeTemplate();

        template.TemplateCode.Should().Be(DecizieRecuperareSumeTemplate.Code);
        template.TemplateCode.Should().Be("decizie-recuperare-sume");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new DecizieRecuperareSumeTemplate();

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
    public void Render_SubstitutesAmountsAndPlaceholders()
    {
        var template = new DecizieRecuperareSumeTemplate();
        var facts = HappyPathFacts();
        facts["beneficiaryFullName"] = "Grigore Recuperarea";
        facts["beneficiaryIdnp"] = "2000999888777";
        facts["dossierSqid"] = "dsr_rec_42";
        facts["overpaidAmountMdl"] = 3450.75m;
        facts["overpaymentReason"] =
            "Plată continuată după decesul beneficiarului — perioadă: ianuarie–martie 2026.";
        facts["recoveryMethod"] = "Reținere lunară 20% din pensia curentă, până la stingere.";
        facts["bankIban"] = "MD24AG000000022500000123";

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("DECIZIE DE RECUPERARE A SUMELOR");
        text.Should().Contain("Grigore Recuperarea");
        text.Should().Contain("2000999888777");
        text.Should().Contain("dsr_rec_42");
        text.Should().Contain("3,450.75 MDL");
        text.Should().Contain("perioadă: ianuarie–martie 2026");
        text.Should().Contain("Reținere lunară 20%");
        text.Should().Contain("MD24AG000000022500000123");
        text.Should().Contain("2026-09-30");
    }

    [Fact]
    public void Render_MissingOverpaidAmount_ReturnsTemplateMissingFacts()
    {
        var template = new DecizieRecuperareSumeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("overpaidAmountMdl");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("overpaidAmountMdl");
    }

    [Fact]
    public void Render_WithoutOptionalBankIban_StillSucceeds()
    {
        // bankIban is optional — render must succeed without it.
        var template = new DecizieRecuperareSumeTemplate();
        var facts = HappyPathFacts();
        facts.Remove("bankIban");

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        text.Should().Contain("Grigore Recuperarea");
    }

    /// <summary>Builds a fact dictionary that satisfies all required keys plus the optional one.</summary>
    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Grigore Recuperarea",
        ["dossierSqid"] = "dsr_rec_001",
        ["overpaidAmountMdl"] = 1200.00m,
        ["overpaymentReason"] = "Sumă plătită în plus în luna ianuarie 2026.",
        ["recoveryMethod"] = "Plată voluntară către contul CNAS.",
        ["repaymentDeadlineUtc"] = RepaymentDeadlineUtc,
        ["bankIban"] = "MD00CN000000000000000000",
    };
}
