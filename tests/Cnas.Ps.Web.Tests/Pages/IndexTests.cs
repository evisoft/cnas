using Bunit;
using Bunit.TestDoubles;
using Cnas.Ps.Web.Pages;
using Cnas.Ps.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>
/// Smoke tests for <see cref="Index"/> — the unauthenticated landing page.
/// </summary>
public sealed class IndexTests : TestContext
{
    /// <summary>
    /// Builds the test context with the minimum services every page needs: localization
    /// and a fake JS runtime so the language-switch onChange handler doesn't crash.
    /// </summary>
    public IndexTests()
    {
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Index_RendersTitleAndLoginButton()
    {
        var cut = RenderComponent<Cnas.Ps.Web.Pages.Index>();

        cut.Find("[data-testid='hero-title']").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.Find("[data-testid='login-btn']").Should().NotBeNull();
        cut.Find("[data-testid='lang-select']").Should().NotBeNull();
    }
}
