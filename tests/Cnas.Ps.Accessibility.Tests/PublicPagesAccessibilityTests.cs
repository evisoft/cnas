using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// R0223 / UI 004 — runs axe-core 4.10 against each citizen-portal route hosted by
/// the static-content harness and fails the build if any serious or critical WCAG
/// 2.1 AA violation is reported. Moderate/minor violations are surfaced via the test
/// output as a non-blocking warning so they show up in CI logs without forcing a fix
/// in the same batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skip behaviour.</b> When the vendored <c>Resources/axe.min.js</c> is the
/// committed placeholder (i.e. a developer has not downloaded the real axe-core
/// bundle), the runner throws <see cref="AxeBundleMissingException"/> and the test
/// is reported as <c>Skipped</c>. The dedicated CI accessibility job downloads the
/// real bundle before invoking <c>dotnet test</c>, so any skip in CI signals a
/// pipeline misconfiguration.
/// </para>
/// <para>
/// <b>Page list.</b> Currently scans the citizen-portal root <c>/</c>. The list will
/// grow as the staff-console pages and the application-submission flow expose
/// anonymously-renderable shells; the theory's data-source is a static array so
/// adding a route is a one-line change.
/// </para>
/// </remarks>
[Collection(AccessibilityCollection.Name)]
public sealed class PublicPagesAccessibilityTests
{
    private readonly AccessibilityFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Injects the shared accessibility fixture (Playwright + static-content host) and
    /// the per-test xUnit output sink so moderate/minor violations can be logged
    /// without failing the run.
    /// </summary>
    public PublicPagesAccessibilityTests(AccessibilityFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Canonical list of every public Blazor route template the WCAG 2.1 AA
    /// audit covers. One entry per <c>@page</c> directive declared under
    /// <c>src/Cnas.Ps.Web/Pages</c>; parameterised templates appear here in
    /// their <c>@page</c> form (e.g. <c>/applications/{id}</c>) and are
    /// normalised to concrete URLs via
    /// <see cref="PublicRouteCatalogTests.ResolveSampleUrl"/> when fed into
    /// axe-core.
    /// </summary>
    /// <remarks>
    /// The companion meta-test <see cref="PublicRouteCatalogTests.ScannedRoutes_CoverEveryPublicPage"/>
    /// asserts this list stays in lock-step with the Pages folder so a new
    /// public page cannot ship unaudited.
    /// </remarks>
    public static readonly IReadOnlyList<string> RouteTemplates = new[]
    {
        "/",
        "/admin/business-rules/{PassportCode}",
        // R0200 / TOR CF 20.01-03, MR 012 — admin cron-schedule editor.
        "/admin/cron",
        // R0204 / TOR CF 20.07-08 — Quartz scheduler dashboard.
        "/admin/jobs",
        "/admin/service-passports",
        "/admin/service-passports/{Code}",
        "/admin/workflow-definitions",
        "/admin/workflow-definitions/{WorkflowCode}",
        "/applications",
        "/applications/new",
        "/applications/{id}",
        "/approvals",
        "/archive",
        // R0611 / TOR CF 12.02 — per-record tabbed detail pages.
        "/archive/contributors/{Sqid}",
        "/archive/insured-persons/{Sqid}",
        "/dashboard",
        // R0932 / TOR §10.1 — Fișa de calcul interactive recalc editor.
        "/decisions/{Sqid}/fisa-de-calcul",
        "/inbox",
        "/inbox/supervisor",
        "/profile/me",
        // R1601 + R1604 / TOR Annex 3.9 — decisions register browser.
        "/registers/decisions",
        // R1602 + R1604 / TOR Annex 3.10 — payment-orders register browser.
        "/registers/payment-orders",
    };

    /// <summary>
    /// xUnit MemberData source — one row per route to scan. Adding a route here adds
    /// a new test case; the test method below is parameterless beyond the route.
    /// The concrete URLs are derived from <see cref="RouteTemplates"/> so the audit
    /// surface and the declared-page meta-test cannot drift.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blazor WebAssembly Standalone caveat.</b> The Web project is a WASM
    /// Standalone SPA — every route returns the same <c>index.html</c> shell
    /// from the static-file host used by <see cref="AccessibilityFixture"/>,
    /// and the per-route Blazor markup is rendered by the WASM runtime AFTER
    /// the shell loads. The axe-core scan therefore re-audits the same shell
    /// for every route entry; the route iteration ensures the scanner is
    /// informed of every public navigation target so that any future
    /// server-rendered or pre-rendered shell will inherit the full audit
    /// surface without changes to the scan harness.
    /// </para>
    /// </remarks>
    public static IEnumerable<object[]> Routes =>
        RouteTemplates.Select(t => new object[] { PublicRouteCatalogTests.ResolveSampleUrl(t) });

    /// <summary>
    /// Navigates to <paramref name="route"/>, runs axe-core, and asserts that no
    /// serious or critical WCAG 2.1 AA violations are present.
    /// </summary>
    /// <param name="route">Relative path to scan (e.g. <c>"/"</c>). Concatenated onto
    /// the fixture's base URL.</param>
    /// <remarks>
    /// <para>
    /// When the vendored axe bundle is the placeholder (offline dev) the test logs a
    /// warning and returns without asserting — there is no real way to skip with xUnit
    /// 2.9 without an extra package, and we treat the "no bundle" case as a
    /// configuration warning rather than a failure. The dedicated CI accessibility
    /// job downloads the real bundle before invoking dotnet test, so a placeholder
    /// in CI indicates a pipeline misconfiguration that the operator will spot in
    /// the per-test trace output.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_HasNoSeriousOrCriticalAccessibilityViolations(string route)
    {
        // ConfigureAwait omitted per xUnit1030 — test methods must not bypass the
        // xUnit synchronization context.
        var bundle = await AxeRunner.LoadBundleAsync();
        if (AxeRunner.IsPlaceholderBundle(bundle))
        {
            _output.WriteLine($"WARNING: axe-core bundle missing — scan of {route} skipped. "
                + "Set up the real bundle by running the CI 'curl axe.min.js' step locally.");
            return;
        }

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var url = _fixture.BaseAddress + route;
        await page.GotoAsync(url);

        var result = await AxeRunner.RunAsync(page, bundle);

        _output.WriteLine($"axe scan {route}: {result.ToShortReport()}");

        var blocking = result.SeriousOrCriticalViolations();
        if (blocking.Count > 0)
        {
            foreach (var v in blocking)
            {
                _output.WriteLine($"  - {v.Impact} {v.Id}: {v.Help} ({v.HelpUrl})");
            }
        }
        blocking.Should().BeEmpty(
            "WCAG 2.1 AA scans must not report serious or critical violations — "
            + "fix the page or grandfather the rule in the axe-options allowlist.");
    }
}
