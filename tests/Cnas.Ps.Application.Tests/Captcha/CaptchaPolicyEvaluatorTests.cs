using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Captcha;

/// <summary>
/// R0507 / TOR CF 01.10 — behaviour tests for
/// <see cref="DefaultCaptchaPolicyEvaluator"/>.
/// </summary>
public sealed class CaptchaPolicyEvaluatorTests
{
    private static PublicCatalogListQueryDto NewQuery(string? q = null, string? category = null) =>
        new(Q: q, Category: category, Sort: "Relevance", Skip: 0, Take: 50, Language: "ro");

    [Fact]
    public void BroadQuery_NoNarrowing_RequiresCaptcha()
    {
        var pol = new DefaultCaptchaPolicyEvaluator();
        pol.RequireCaptcha(NewQuery()).Should().BeTrue();
    }

    [Fact]
    public void NarrowQuery_WithFreeText_DoesNotRequireCaptcha()
    {
        var pol = new DefaultCaptchaPolicyEvaluator();
        pol.RequireCaptcha(NewQuery(q: "pension")).Should().BeFalse();
    }

    [Fact]
    public void NarrowQuery_WithCategory_DoesNotRequireCaptcha()
    {
        var pol = new DefaultCaptchaPolicyEvaluator();
        pol.RequireCaptcha(NewQuery(category: "PENSIONS")).Should().BeFalse();
    }

    [Fact]
    public void NullQuery_DefensiveDefault_RequiresCaptcha()
    {
        var pol = new DefaultCaptchaPolicyEvaluator();
        pol.RequireCaptcha(null).Should().BeTrue();
    }
}
