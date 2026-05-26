using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="DispozitiaInscriereTemplate"/> — Annex 7
/// "Dispoziție privind înscrierea" template.
/// </summary>
public class DispozitiaInscriereTemplateTests
{
    private static readonly DateTime EffectiveFromUtc = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_HappyPath_ProducesDocxWithMagicBytes()
    {
        var template = new DispozitiaInscriereTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_RendersAllClauses()
    {
        var template = new DispozitiaInscriereTemplate();
        var clauses = new List<string>
        {
            "Se înscrie persoana în registrul beneficiarilor.",
            "Plata se efectuează lunar la cont bancar.",
            "Decizia este executorie de la data semnării.",
        };
        var facts = HappyPathFacts();
        facts["clauses"] = clauses;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value);
        foreach (var clause in clauses)
        {
            text.Should().Contain(clause);
        }
    }

    [Fact]
    public void Render_MissingClauses_ReturnsTemplateMissingFacts()
    {
        var template = new DispozitiaInscriereTemplate();
        var facts = HappyPathFacts();
        facts.Remove("clauses");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["beneficiaryIdnp"] = "2000000000007",
        ["beneficiaryFullName"] = "Ion Popescu",
        ["effectiveFromUtc"] = EffectiveFromUtc,
        ["clauses"] = new List<string> { "Clauza A", "Clauza B" },
    };
}
