using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Tests.Documents.Templates;

/// <summary>
/// Unit tests for <see cref="RaportAuditActivitateTemplate"/> — the Annex 7
/// audit-activity report template (R2002 §8.7.3.3).
/// </summary>
public sealed class RaportAuditActivitateTemplateTests
{
    private static readonly DateTime FromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TemplateCode_Property_MatchesExpected()
    {
        var template = new RaportAuditActivitateTemplate();

        template.TemplateCode.Should().Be(RaportAuditActivitateTemplate.Code);
        template.TemplateCode.Should().Be("raport-audit-activitate");
    }

    [Fact]
    public void Render_HappyPath_ProducesValidDocx()
    {
        var template = new RaportAuditActivitateTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Length.Should().BeGreaterThan(4);
        result.Value[0].Should().Be(0x50);
        result.Value[1].Should().Be(0x4B);
        result.Value[2].Should().Be(0x03);
        result.Value[3].Should().Be(0x04);
    }

    [Fact]
    public void Render_RendersPerActorRows()
    {
        var template = new RaportAuditActivitateTemplate();

        var result = template.Render(HappyPathFacts());

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value!);
        text.Should().Contain("SQID-USER-1");
        text.Should().Contain("SQID-USER-2");
        text.Should().Contain("42");
        text.Should().Contain("17");
    }

    [Fact]
    public void Render_CriticalCountGreaterThanZero_RendersHighlight()
    {
        var template = new RaportAuditActivitateTemplate();
        var facts = HappyPathFacts();
        facts["criticalEventCount"] = 3;

        var result = template.Render(facts);

        result.IsSuccess.Should().BeTrue();
        var text = DeciziaPensieTemplateTests.ExtractAllText(result.Value!);
        text.Should().Contain("ATENȚIE");
        text.Should().Contain("3");
    }

    [Fact]
    public void Render_MissingActorRows_ReturnsTemplateMissingFacts()
    {
        var template = new RaportAuditActivitateTemplate();
        var facts = HappyPathFacts();
        facts.Remove("actorRows");

        var result = template.Render(facts);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateMissingFacts);
        result.ErrorMessage.Should().Contain("actorRows");
    }

    private static Dictionary<string, object?> HappyPathFacts() => new()
    {
        ["fromUtc"] = FromUtc,
        ["toUtc"] = ToUtc,
        ["actorRows"] = new List<RaportAuditActivitateTemplate.ActorRow>
        {
            new("SQID-USER-1", 42),
            new("SQID-USER-2", 17),
        },
    };
}
