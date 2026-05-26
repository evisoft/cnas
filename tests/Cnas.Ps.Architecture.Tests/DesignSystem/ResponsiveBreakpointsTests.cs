using System.IO;
using System.Text.RegularExpressions;

namespace Cnas.Ps.Architecture.Tests.DesignSystem;

/// <summary>
/// Configuration-locking tests for the HG 677/2025 responsive grid + breakpoint
/// scale (CLAUDE.md cardinal rules; TODO.md R0222, TOR UI 005-006).
///
/// Why an architecture test, not a runtime test:
///   * The responsive breakpoint scale is structural contract that EVERY Blazor
///     page consumes. If a token disappears or the file is renamed, layout silently
///     falls back to browser defaults at narrow viewports — no compile-time signal,
///     no test failure beyond visual regression.
///   * The CSS literal pixel values in <c>@media</c> rules MUST stay in sync with
///     the <c>--cnas-bp-*</c> reference tokens. CSS Custom Properties cannot be
///     used inside <c>@media</c> conditions, so drift between the two is a real
///     risk and not caught by the CSS parser. We pin both sides here.
///   * The <c>index.html</c> link tag for <c>cnas-responsive.css</c> must load
///     AFTER the token sheet (so utilities can dereference <c>var(--cnas-space-4)</c>
///     etc.) and BEFORE app styles. Pin the relative order.
///
/// Mirrors the file-on-disk inspection pattern used by
/// <see cref="HgDesignTokensTests"/> (R0221) — no DOM, no rendering, fast in CI.
///
/// Scope of this batch (R0222):
///   * Pin presence of the five <c>--cnas-bp-*</c> tokens.
///   * Pin existence of <c>cnas-responsive.css</c> at the expected path and the
///     presence of its core <c>.cnas-container</c> rule with <c>max-width: 1360px</c>.
///   * Pin presence of <c>@media (min-width: 640px)</c> and
///     <c>@media (min-width: 1360px)</c> blocks (smallest enabled breakpoint and
///     baseline desktop, respectively).
///   * Pin link-tag ordering in <c>index.html</c>.
/// </summary>
public class ResponsiveBreakpointsTests
{
    /// <summary>
    /// Path (relative to repo root) of the HG 677/2025 design-token CSS file. Reused
    /// from <see cref="HgDesignTokensTests"/> — duplicated as a private constant
    /// here so this test class stands alone and a refactor that moves the file must
    /// update both classes (deliberate redundancy: independent failure messages).
    /// </summary>
    private const string TokensCssRelativePath = "src/Cnas.Ps.Web/wwwroot/css/hg677-tokens.css";

    /// <summary>
    /// Path (relative to repo root) of the responsive grid / utilities CSS file.
    /// </summary>
    private const string ResponsiveCssRelativePath = "src/Cnas.Ps.Web/wwwroot/css/cnas-responsive.css";

    /// <summary>
    /// Path (relative to repo root) of the citizen-portal entry HTML that links the
    /// responsive sheet.
    /// </summary>
    private const string IndexHtmlRelativePath = "src/Cnas.Ps.Web/wwwroot/index.html";

    [Fact]
    public void TokensCss_Declares_All_BreakpointTokens()
    {
        // The five reference-only tokens MUST exist so the design-system docs and
        // the responsive sheet share one scale. Missing tokens are documentation
        // drift, not a runtime failure — but they cause guesswork for future work.
        var css = ReadTokensCss();

        foreach (var (token, label) in new[]
        {
            ("--cnas-bp-sm",  "small phones landscape"),
            ("--cnas-bp-md",  "tablet portrait"),
            ("--cnas-bp-lg",  "tablet landscape / small laptop"),
            ("--cnas-bp-xl",  "BASELINE desktop (1360 px)"),
            ("--cnas-bp-2xl", "full HD desktop / wide monitor"),
        })
        {
            css.Should().Contain(token,
                $"breakpoint token {token} ({label}) is part of the documented scale; " +
                "removing it would silently break the responsive contract documented in " +
                "docs/design-system.md.");
        }
    }

    [Fact]
    public void ResponsiveCss_Exists_At_Expected_Path()
    {
        // Gate 1: the file must exist. Every subsequent assertion depends on this;
        // calling it out explicitly produces a single targeted failure instead of a
        // cascade of misleading "string not found" failures further down.
        var path = Path.Combine(LocateRepoRoot(), ResponsiveCssRelativePath);

        File.Exists(path).Should().BeTrue(
            $"HG 677/2025 responsive utilities must live at '{ResponsiveCssRelativePath}'. " +
            "Without this file the citizen portal has no documented breakpoint scaffolding.");
    }

    [Fact]
    public void ResponsiveCss_Defines_Container_With_BaselineMaxWidth()
    {
        // The .cnas-container max-width pin is the LOAD-BEARING assertion of this
        // batch: it's the single literal that anchors the entire baseline-viewport
        // contract. Whitespace-insensitive match — formatter tweaks (one vs. two
        // spaces, tabs vs. spaces) must not break the test.
        var css = ReadResponsiveCss();

        // Strip whitespace AROUND the colon and semicolon so we match
        // "max-width:1360px" and "max-width: 1360px" alike.
        var compact = Regex.Replace(css, @"\s+", " ");
        compact.Should().Contain(".cnas-container",
            "the responsive sheet must define the .cnas-container utility class.");
        compact.Should().MatchRegex(@"\.cnas-container[^{]*\{[^}]*max-width:\s*1360px",
            "the .cnas-container utility MUST declare max-width: 1360px so the citizen " +
            "portal never exceeds the documented HG 677 / TOR UI 005-006 baseline viewport.");
    }

    [Fact]
    public void ResponsiveCss_Declares_SmBreakpoint_MediaQuery()
    {
        // The 640 px (sm) block is the smallest enabled breakpoint — it is where the
        // grid columns leave mobile-first stack mode and adopt percentage widths.
        // Without it, the layout silently degrades at every viewport.
        var css = ReadResponsiveCss();
        var hasSmBlock = Regex.IsMatch(css, @"@media\s*\(\s*min-width\s*:\s*640px\s*\)");

        hasSmBlock.Should().BeTrue(
            "the responsive sheet must include a '@media (min-width: 640px)' block — " +
            "this is the smallest enabled breakpoint and where the grid leaves mobile-first " +
            "stacking. Removing it collapses all viewports into the same stacked layout.");
    }

    [Fact]
    public void ResponsiveCss_Declares_XlBaselineBreakpoint_MediaQuery()
    {
        // The 1360 px (xl) block is the BASELINE viewport per TOR UI 005-006. Any
        // refactor that removes it must explicitly justify why the baseline target
        // no longer needs a media query — almost certainly that means the baseline
        // has changed, which is a TOR change that requires updating CLAUDE.md +
        // docs/design-system.md, not silently dropping the test.
        var css = ReadResponsiveCss();
        var hasXlBlock = Regex.IsMatch(css, @"@media\s*\(\s*min-width\s*:\s*1360px\s*\)");

        hasXlBlock.Should().BeTrue(
            "the responsive sheet must include a '@media (min-width: 1360px)' block — " +
            "1360 px is the SI Protecția Socială baseline desktop viewport per TOR UI 005-006. " +
            "Changing the baseline requires updating both CLAUDE.md and docs/design-system.md.");
    }

    [Fact]
    public void IndexHtml_References_ResponsiveCss_After_TokensCss()
    {
        // Link order matters: the responsive sheet uses var(--cnas-space-4) and
        // friends, so the tokens MUST be parsed first. We assert both link tags
        // exist and the responsive one appears AFTER the tokens link.
        var indexPath = Path.Combine(LocateRepoRoot(), IndexHtmlRelativePath);
        File.Exists(indexPath).Should().BeTrue(
            $"the citizen-portal entry HTML must exist at '{IndexHtmlRelativePath}'.");

        var html = File.ReadAllText(indexPath);

        var tokensIdx = html.IndexOf("css/hg677-tokens.css", StringComparison.Ordinal);
        tokensIdx.Should().BeGreaterThanOrEqualTo(0,
            "index.html must reference the design tokens via <link href=\"css/hg677-tokens.css\" ... >.");

        var responsiveIdx = html.IndexOf("css/cnas-responsive.css", StringComparison.Ordinal);
        responsiveIdx.Should().BeGreaterThanOrEqualTo(0,
            "index.html must reference the responsive utilities via " +
            "<link href=\"css/cnas-responsive.css\" ... >.");

        tokensIdx.Should().BeLessThan(responsiveIdx,
            "cnas-responsive.css must load AFTER hg677-tokens.css so the utility classes can " +
            "dereference the --cnas-space-* and --cnas-bp-* tokens during cascade resolution.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the repo root,
    /// identified by the <c>Cnas.Ps.slnx</c> solution file. Mirrors the locator in
    /// <see cref="HgDesignTokensTests"/> and
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
    /// Reads <see cref="TokensCssRelativePath"/> from the repo root. Helper kept
    /// private + small so individual tests stay focused on one assertion.
    /// </summary>
    /// <returns>The full text of the tokens CSS file.</returns>
    private static string ReadTokensCss()
    {
        var path = Path.Combine(LocateRepoRoot(), TokensCssRelativePath);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Reads <see cref="ResponsiveCssRelativePath"/> from the repo root. Helper
    /// kept private + small so individual tests stay focused on one assertion.
    /// </summary>
    /// <returns>The full text of the responsive utilities CSS file.</returns>
    private static string ReadResponsiveCss()
    {
        var path = Path.Combine(LocateRepoRoot(), ResponsiveCssRelativePath);
        return File.ReadAllText(path);
    }
}
