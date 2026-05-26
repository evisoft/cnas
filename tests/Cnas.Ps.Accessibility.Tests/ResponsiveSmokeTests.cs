using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// R0222 / TOR UI 005-006 — viewport smoke tests. Boots the static citizen-portal
/// shell through the shared <see cref="AccessibilityFixture"/> Playwright + Kestrel
/// pair and confirms the page renders without crashing at four representative
/// viewport sizes. These are SMOKE tests — they verify rendering survives the
/// breakpoint stops, NOT pixel-perfect layout. Pixel comparison (visual regression)
/// is deferred until the component library lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why smoke tests, not assertions on layout.</b> Pixel-perfect comparison
/// needs golden screenshots, headless-rendering determinism (font-AA, GPU
/// flags), and a per-OS baseline. That investment is out of scope for R0222 —
/// the contract this batch ships is "<i>the citizen portal does not crash or
/// produce zero-width documents at the four documented viewports</i>", which
/// is sufficient to catch the most common regression (a CSS rule that sets a
/// huge fixed width or breaks the document model).
/// </para>
/// <para>
/// <b>Viewport list — four canonical sizes.</b>
/// </para>
/// <list type="bullet">
///   <item><c>1360 × 768</c> — BASELINE per TOR UI 005-006. Smallest supported desktop.</item>
///   <item><c>1920 × 1080</c> — Full HD desktop / wide monitor.</item>
///   <item><c>768 × 1024</c>  — Tablet portrait (iPad-class).</item>
///   <item><c>375 × 667</c>   — Small phone (iPhone-class, narrowest target).</item>
/// </list>
/// <para>
/// <b>Skip behaviour.</b> Mirrors <see cref="PublicPagesAccessibilityTests"/> — if
/// the accessibility fixture's static-content host or the headless Chromium
/// browser fails to launch (a sandbox/CI misconfiguration rather than a real
/// product regression), the test logs a warning and returns successfully. The
/// dedicated accessibility CI job exercises the fixture properly so a genuine
/// fixture outage is caught there.
/// </para>
/// </remarks>
[Collection(AccessibilityCollection.Name)]
public sealed class ResponsiveSmokeTests
{
    private readonly AccessibilityFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Injects the shared accessibility fixture (Playwright + static-content host)
    /// and the per-test xUnit output sink so per-viewport diagnostics surface in
    /// the CI trace.
    /// </summary>
    public ResponsiveSmokeTests(AccessibilityFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// xUnit MemberData source — one row per viewport tuple <c>(width, height,
    /// label)</c>. The label only affects the test-display name; the assertion
    /// runs against width + height.
    /// </summary>
    public static IEnumerable<object[]> Viewports => new[]
    {
        new object[] { 1360, 768,  "baseline-desktop-1360x768" },
        new object[] { 1920, 1080, "wide-desktop-1920x1080" },
        new object[] { 768,  1024, "tablet-portrait-768x1024" },
        new object[] { 375,  667,  "small-phone-375x667" },
    };

    /// <summary>
    /// Navigates the static citizen-portal shell at the supplied viewport and
    /// asserts: (a) the body element exists, (b) the document has a positive
    /// scrollWidth. The screenshot is captured purely as a diagnostic
    /// side-channel — it is not compared, just produced so CI can attach the
    /// binary to the test-run for manual review when something else fails.
    /// </summary>
    /// <param name="width">Viewport width in CSS pixels.</param>
    /// <param name="height">Viewport height in CSS pixels.</param>
    /// <param name="label">Human-readable viewport label, used in trace output.</param>
    /// <remarks>
    /// <para>
    /// <b>Why <c>scrollWidth &gt; 0</c> instead of <c>== width</c>?</b> A horizontal
    /// scroll bar at narrow viewports is acceptable for tables / dense
    /// staff-console pages; what we MUST catch is a degenerate document
    /// (scrollWidth == 0) which means the body collapsed entirely. The "no
    /// horizontal scroll at the baseline" rule will land with the per-page
    /// visual regression tests, not here.
    /// </para>
    /// <para>
    /// <b>Skip semantics.</b> Mirrors R0223 placeholder handling — if the
    /// fixture's <see cref="AccessibilityFixture.BaseAddress"/> is empty (host
    /// failed to start) or the browser is null, log + return so offline dev
    /// runs aren't blocked by infrastructure quirks. Real fixture failures are
    /// caught by the dedicated accessibility CI job.
    /// </para>
    /// </remarks>
    [Theory(DisplayName = "Page renders without layout collapse at viewport")]
    [MemberData(nameof(Viewports))]
    public async Task Page_Renders_At_Viewport(int width, int height, string label)
    {
        if (string.IsNullOrEmpty(_fixture.BaseAddress) || _fixture.Browser is null)
        {
            // Mirrors R0223 placeholder handling — log and return on fixture quirks
            // (sandbox without Chromium, CI without browser deps) so offline dev
            // runs aren't blocked. The dedicated accessibility CI job validates
            // the fixture properly.
            _output.WriteLine(
                $"WARNING: accessibility fixture not ready — viewport {label} skipped. "
                + "The CI accessibility job validates the fixture properly.");
            return;
        }

        // One context per test so viewports cannot pollute each other's storage,
        // cookies, or service-worker state. Cheap (a few ms) — Playwright reuses
        // the underlying browser process across contexts.
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();

        var url = _fixture.BaseAddress + "/";
        await page.GotoAsync(url);

        // Diagnostic side-channel — discard the bytes, but the capture forces
        // Playwright to wait for a paint to land before we measure scrollWidth.
        // Capturing into memory (no file path) means we don't litter the
        // test-output directory with PNGs.
        _ = await page.ScreenshotAsync();

        // Assertion 1 — the body element must exist. A missing <body> means the
        // shell failed to load (404 on index.html, a JS-throws-on-parse error,
        // or Playwright navigated to about:blank by mistake).
        var bodyCount = await page.Locator("body").CountAsync();
        bodyCount.Should().Be(1,
            $"viewport {label} ({width}x{height}): a single <body> must render. " +
            "A count of 0 means the static shell failed to load; > 1 means an iframe " +
            "or Shadow-DOM mishap leaked an inner document.");

        // Assertion 2 — the document must have a positive scrollWidth. Zero
        // scrollWidth = degenerate layout (whole document collapsed); we don't
        // assert == viewport width because dense pages legitimately produce a
        // horizontal scroll on narrow viewports.
        var scrollWidth = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth");
        scrollWidth.Should().BeGreaterThan(0,
            $"viewport {label} ({width}x{height}): document.documentElement.scrollWidth " +
            "must be > 0. A zero value means the layout collapsed entirely — almost " +
            "certainly a CSS rule that set width or display incorrectly at this breakpoint.");

        _output.WriteLine(
            $"Viewport {label} ({width}x{height}): scrollWidth={scrollWidth}px — OK.");
    }
}
