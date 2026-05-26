using System.IO;
using System.Text.RegularExpressions;

namespace Cnas.Ps.Architecture.Tests.DesignSystem;

/// <summary>
/// Configuration-locking tests for the HG 677/2025 "Modelul Unitar de Design" CSS token sheet
/// (CLAUDE.md cardinal rules; TODO.md R0221, TOR UI 016).
///
/// Why an architecture test, not a runtime test:
///   * The HG 677/2025 design-token CSS is a structural contract that ALL Blazor pages
///     consume. If the file disappears, is renamed, or loses one of the documented token
///     groups (colour, typography, spacing, radius, shadow, z-index) then every page that
///     dereferences <c>var(--cnas-color-primary-500)</c> silently falls back to the
///     browser default — visual regression with no compile-time signal.
///   * The token file is also imported by <c>index.html</c>; if the link tag is dropped,
///     the tokens load nowhere and the override sheet stops working. We pin the link tag
///     here too.
///
/// The tests inspect the on-disk files (no DOM, no rendering) the same way
/// <see cref="Cnas.Ps.Architecture.Tests.Hooks.HuskyConfigurationTests"/> does for the
/// Husky chain — they are fast and run on every PR.
///
/// Scope of this batch (R0221):
///   * Token vocabulary is locked: presence of colour-primary scale (≥ 5 stops),
///     sans font family, spacing scale 1-6, dark-mode media query, and the import in
///     index.html. Specific HEX values and typography metrics are NOT pinned — they may
///     be tuned as the real HG 677 palette PDF is parsed in a follow-up batch.
/// </summary>
public class HgDesignTokensTests
{
    /// <summary>
    /// Path (relative to repo root) of the HG 677/2025 design-token CSS file.
    /// Pinned here so a refactor that moves the file under <c>wwwroot/</c> must update this constant.
    /// </summary>
    private const string TokensCssRelativePath = "src/Cnas.Ps.Web/wwwroot/css/hg677-tokens.css";

    /// <summary>
    /// Path (relative to repo root) of the citizen-portal entry HTML that imports the tokens.
    /// </summary>
    private const string IndexHtmlRelativePath = "src/Cnas.Ps.Web/wwwroot/index.html";

    [Fact]
    public void TokensCss_Exists_At_Expected_Path()
    {
        // Gate 1: the file must exist. Every subsequent assertion depends on this; calling
        // it out explicitly makes a missing file produce a single targeted failure instead
        // of a cascade of misleading "string not found" failures.
        var path = Path.Combine(LocateRepoRoot(), TokensCssRelativePath);

        File.Exists(path).Should().BeTrue(
            $"HG 677/2025 design tokens must live at '{TokensCssRelativePath}'. " +
            "Without this file the citizen portal has no canonical design system.");
    }

    [Fact]
    public void TokensCss_Defines_RootSelector_Block()
    {
        // The token vocabulary MUST live on the global :root selector so the variables
        // resolve from anywhere in the cascade. Component-scoped tokens would defeat the
        // unified-design mandate.
        var css = ReadTokensCss();

        css.Should().Contain(":root {",
            "design tokens must be declared on the ':root' selector so all components inherit them. " +
            "Scoping tokens to specific selectors defeats the HG 677 unified-design goal.");
    }

    [Fact]
    public void TokensCss_Defines_AtLeast_FiveColorPrimaryStops()
    {
        // The HG 677 unified design system expects a Tailwind/Material-style stepped palette
        // (50/100/200…900) so contrast pairings work in both light and dark mode. We pin
        // the MINIMUM five stops (100/300/500/700/900) — additional stops are fine.
        var css = ReadTokensCss();
        var stops = Regex.Matches(css, "--cnas-color-primary-\\d+\\s*:");

        stops.Count.Should().BeGreaterThanOrEqualTo(5,
            "the primary colour scale must define at least 5 stops (e.g. 100/300/500/700/900) " +
            $"so light/dark mode pairings have enough contrast steps. Found {stops.Count} stop(s).");
    }

    [Fact]
    public void TokensCss_Defines_FontFamilySans()
    {
        // Sans-serif typography is the HG 677 baseline; serif is reserved for legal-document
        // contexts. We pin sans because every interactive page uses it.
        var css = ReadTokensCss();

        css.Should().Contain("--cnas-font-family-sans",
            "the sans-serif font family token anchors the body text of every page; missing it " +
            "would force every component to redeclare a system font stack.");
    }

    [Fact]
    public void TokensCss_Defines_SpacingScale_OneThroughSix()
    {
        // The 4 px spacing scale produces predictable rhythm. We pin 1-6 (the most-used
        // small/medium spacings); larger stops (8/10/12/16/20/24) are documented in the
        // design-system docs but not pinned here to allow future tuning.
        var css = ReadTokensCss();

        foreach (var step in new[] { 1, 2, 3, 4, 5, 6 })
        {
            css.Should().Contain($"--cnas-space-{step}",
                $"spacing token --cnas-space-{step} is part of the documented 4 px scale; " +
                "removing it would break every component that depends on consistent rhythm.");
        }
    }

    [Fact]
    public void TokensCss_Includes_DarkMode_MediaQuery()
    {
        // HG 677 mandates WCAG AA contrast in both colour schemes. We don't pin specific
        // values; we pin the existence of the @media prefers-color-scheme: dark block so
        // an accidental delete (or "I disabled dark mode while debugging") is caught.
        var css = ReadTokensCss();

        var hasDarkBlock = Regex.IsMatch(css, "@media\\s*\\(\\s*prefers-color-scheme\\s*:\\s*dark\\s*\\)");
        hasDarkBlock.Should().BeTrue(
            "the token sheet must include a '@media (prefers-color-scheme: dark)' block. " +
            "HG 677/2025 + WCAG AA require both light and dark mode contrast guarantees (see R0223).");
    }

    [Fact]
    public void IndexHtml_References_TokensCss_BeforeSiteCss()
    {
        // The link order matters: tokens must load FIRST so the app's site.css can override
        // them where needed. Reversing the order causes the cascade to favour tokens over
        // bespoke overrides — the opposite of intended behaviour.
        var indexPath = Path.Combine(LocateRepoRoot(), IndexHtmlRelativePath);
        File.Exists(indexPath).Should().BeTrue(
            $"the citizen-portal entry HTML must exist at '{IndexHtmlRelativePath}'.");

        var html = File.ReadAllText(indexPath);

        var tokensIdx = html.IndexOf("css/hg677-tokens.css", StringComparison.Ordinal);
        tokensIdx.Should().BeGreaterThanOrEqualTo(0,
            "index.html must reference the design tokens via <link href=\"css/hg677-tokens.css\" ... >.");

        var siteIdx = html.IndexOf("css/site.css", StringComparison.Ordinal);
        if (siteIdx >= 0)
        {
            // If both stylesheets are present, the tokens must load first.
            tokensIdx.Should().BeLessThan(siteIdx,
                "tokens must load before site.css so the app stylesheet can override token defaults. " +
                "Reversing the order makes overrides ineffective.");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the repo root,
    /// identified by the <c>Cnas.Ps.slnx</c> solution file. Mirrors the locator in
    /// <see cref="Cnas.Ps.Architecture.Tests.Hooks.HuskyConfigurationTests"/>.
    /// </summary>
    /// <returns>Absolute path of the repository root.</returns>
    /// <exception cref="DirectoryNotFoundException">If the root marker is not found.</exception>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cnas.Ps.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repository root (looked for sibling Cnas.Ps.slnx file starting from " +
            $"{AppContext.BaseDirectory}).");
    }

    /// <summary>
    /// Reads <see cref="TokensCssRelativePath"/> from the repo root.
    /// Helper kept private + small so individual tests stay focused on one assertion.
    /// </summary>
    /// <returns>The full text of the tokens CSS file.</returns>
    private static string ReadTokensCss()
    {
        var path = Path.Combine(LocateRepoRoot(), TokensCssRelativePath);
        return File.ReadAllText(path);
    }
}
