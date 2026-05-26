using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// R2704 — verifies that the axe-core scan's route table covers every public
/// Blazor page declared in <c>src/Cnas.Ps.Web/Pages</c>. A "public" page is one
/// that does NOT carry an <c>[Authorize]</c> attribute on the razor component
/// (Blazor WebAssembly Standalone gates content at runtime inside each page,
/// so the route itself is anonymously reachable — the WCAG scan still has to
/// audit the anonymous shell the citizen lands on before signing in).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a meta-test, not just hard-coded routes?</b> The route table in
/// <see cref="PublicPagesAccessibilityTests.Routes"/> can silently fall behind
/// the Pages folder — a new <c>@page "/foo"</c> directive lands, the scan keeps
/// auditing the same five pages, and the new route ships unaudited. This test
/// reads the Pages folder at test time, extracts every <c>@page "..."</c>
/// directive, and asserts that the scanned route set covers it.
/// </para>
/// <para>
/// <b>What is excluded?</b> Routes that contain a route parameter
/// (e.g. <c>/applications/{id}</c>) are normalised to a representative URL
/// (<c>/applications/sample</c>) because axe-core needs a concrete navigation
/// target. The mapping table lives in <see cref="ParameterizedRouteSamples"/>;
/// adding a new parameterised page requires also adding the sample so the
/// meta-test stays green.
/// </para>
/// <para>
/// <b>Blazor WASM caveat.</b> Every route in a WebAssembly Standalone app
/// returns the same <c>index.html</c> shell — the per-route Blazor markup is
/// rendered by the WASM runtime after bootstrap. The static-shell scan
/// therefore re-audits the same DOM for every route; the route coverage proves
/// the scanner has been informed of every public navigation target so that any
/// future server-rendered or pre-rendered shell will inherit the full table.
/// </para>
/// </remarks>
public sealed class PublicRouteCatalogTests
{
    /// <summary>
    /// Maps route templates with parameters (e.g. <c>/applications/{id}</c>) to
    /// concrete sample URLs that axe-core can navigate. The dictionary key is
    /// the @page template exactly as written in the .razor file.
    /// </summary>
    /// <remarks>
    /// Adding a new parameterised page WITHOUT updating this dictionary will
    /// cause <see cref="ScannedRoutes_CoverEveryPublicPage"/> to fail with a
    /// "no concrete sample for &lt;template&gt;" message — that is the intended
    /// hand-off: the new page author has to declare an audit-safe URL for the
    /// route before the scan ships green.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> ParameterizedRouteSamples =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/applications/{id}"] = "/applications/sample",
            ["/admin/workflow-definitions/{WorkflowCode}"] = "/admin/workflow-definitions/sample",
            // R0141 / TOR CF 15.03 — sample URL for the per-passport business-rule editor.
            ["/admin/business-rules/{PassportCode}"] = "/admin/business-rules/sample",
            // R0640 / TOR CF 15.01-15.04 — sample URL for the per-passport admin editor.
            ["/admin/service-passports/{Code}"] = "/admin/service-passports/sample",
            // R0611 / TOR CF 12.02 — sample URLs for the per-record tabbed detail pages.
            ["/archive/contributors/{Sqid}"] = "/archive/contributors/sample",
            ["/archive/insured-persons/{Sqid}"] = "/archive/insured-persons/sample",
            // R0932 / TOR §10.1 — sample URL for the Fișa de calcul recalc editor.
            ["/decisions/{Sqid}/fisa-de-calcul"] = "/decisions/sample/fisa-de-calcul",
        };

    /// <summary>
    /// Regex that captures the route template from a single
    /// <c>@page "..."</c> directive. The .razor source is parsed line-by-line
    /// so a stray <c>@page</c> in a multi-line comment does not produce a false
    /// positive.
    /// </summary>
    private static readonly Regex PageDirectiveRegex = new(
        @"^\s*@page\s+""(?<route>[^""]+)""\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Locates the repository root by walking up from the test-assembly
    /// directory looking for the <c>Cnas.Ps.slnx</c> file.
    /// </summary>
    /// <returns>Absolute path to the repo root.</returns>
    /// <exception cref="InvalidOperationException">If the repo root cannot be
    /// located — almost always indicates a broken test runner working dir.</exception>
    private static string GetRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(PublicRouteCatalogTests).Assembly.Location)
            ?? throw new InvalidOperationException("Test assembly location is null.");
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cnas.Ps.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Repository root not found — walked up from the running assembly looking for Cnas.Ps.slnx.");
    }

    /// <summary>
    /// Scans every <c>*.razor</c> file under <c>src/Cnas.Ps.Web/Pages</c> and
    /// returns the set of <c>@page</c> templates declared therein.
    /// </summary>
    /// <returns>Distinct, ordinally-sorted route templates.</returns>
    public static IReadOnlyList<string> EnumerateDeclaredRoutes()
    {
        var repoRoot = GetRepoRoot();
        var pagesDir = Path.Combine(repoRoot, "src", "Cnas.Ps.Web", "Pages");
        if (!Directory.Exists(pagesDir))
        {
            throw new InvalidOperationException(
                $"Pages directory not found: {pagesDir}. The repository layout must include the Blazor pages "
                + "at the canonical path for the meta-test to enumerate them.");
        }

        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var razorFile in Directory.EnumerateFiles(pagesDir, "*.razor", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(razorFile))
            {
                var match = PageDirectiveRegex.Match(line);
                if (match.Success)
                {
                    routes.Add(match.Groups["route"].Value);
                }
            }
        }
        return routes.ToList();
    }

    /// <summary>
    /// Maps a possibly-parameterised route template to a concrete URL the
    /// axe-core scan can navigate. Non-parameterised routes pass through
    /// unchanged; parameterised templates are looked up in
    /// <see cref="ParameterizedRouteSamples"/>.
    /// </summary>
    /// <param name="template">The @page route template (e.g. <c>/applications/{id}</c>).</param>
    /// <returns>The concrete URL to scan.</returns>
    /// <exception cref="InvalidOperationException">When a parameterised
    /// template has no entry in the sample map — see remarks on
    /// <see cref="ParameterizedRouteSamples"/>.</exception>
    public static string ResolveSampleUrl(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            throw new ArgumentException("template must be non-empty.", nameof(template));
        }
        if (!template.Contains('{'))
        {
            return template;
        }
        if (ParameterizedRouteSamples.TryGetValue(template, out var sample))
        {
            return sample;
        }
        throw new InvalidOperationException(
            $"No concrete sample URL declared for parameterised route '{template}'. "
            + $"Add an entry to {nameof(PublicRouteCatalogTests)}.{nameof(ParameterizedRouteSamples)} so the "
            + "axe-core scan can navigate this route.");
    }

    /// <summary>
    /// Every <c>@page</c> directive under <c>src/Cnas.Ps.Web/Pages</c> must be
    /// represented in the axe-core route table (modulo parameterised-route
    /// normalisation). Failure means a new public page shipped without WCAG
    /// audit coverage.
    /// </summary>
    [Fact]
    public void ScannedRoutes_CoverEveryPublicPage()
    {
        var declared = EnumerateDeclaredRoutes();
        var scannedTemplates = PublicPagesAccessibilityTests.RouteTemplates;

        declared.Should().NotBeEmpty(
            "the citizen portal must declare at least one @page directive — an empty set means the "
            + "Pages directory enumeration is broken, not that the app has no pages.");

        // Every parameterised template encountered must have a sample URL declared.
        foreach (var template in declared.Where(r => r.Contains('{')))
        {
            ParameterizedRouteSamples.Should().ContainKey(template,
                $"the parameterised route '{template}' needs a concrete sample URL declared in "
                + $"{nameof(ParameterizedRouteSamples)} so axe-core can navigate it.");
        }

        // The scanned-template set must equal the declared-page set so an
        // unscanned new page fails the build rather than ship silently.
        scannedTemplates.Should().BeEquivalentTo(declared,
            "every public Blazor @page must appear in PublicPagesAccessibilityTests.RouteTemplates "
            + "so axe-core audits every route. Add missing routes to the Routes member data.");
    }

    /// <summary>
    /// <see cref="ResolveSampleUrl"/> must round-trip a non-parameterised route
    /// unchanged — the axe-core scan navigates the literal route in that case.
    /// </summary>
    [Fact]
    public void ResolveSampleUrl_ReturnsLiteralRoute_WhenNoParameter()
    {
        ResolveSampleUrl("/inbox").Should().Be("/inbox");
        ResolveSampleUrl("/").Should().Be("/");
        ResolveSampleUrl("/admin/workflow-definitions").Should().Be("/admin/workflow-definitions");
    }

    /// <summary>
    /// <see cref="ResolveSampleUrl"/> must substitute a registered sample for
    /// every parameterised template; an unregistered template must surface as
    /// an explicit failure rather than a silent fall-through.
    /// </summary>
    [Fact]
    public void ResolveSampleUrl_SubstitutesSampleForParameterisedRoute()
    {
        ResolveSampleUrl("/applications/{id}").Should().Be("/applications/sample");
        ResolveSampleUrl("/admin/workflow-definitions/{WorkflowCode}")
            .Should().Be("/admin/workflow-definitions/sample");
    }

    /// <summary>
    /// <see cref="ResolveSampleUrl"/> must throw with a clear message when a
    /// parameterised template has no registered sample. The throw is the
    /// hand-off mechanism — a new parameterised page cannot ship until its
    /// sample URL is declared.
    /// </summary>
    [Fact]
    public void ResolveSampleUrl_Throws_WhenParameterisedTemplateHasNoSample()
    {
        var act = () => ResolveSampleUrl("/orders/{orderId}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No concrete sample URL declared for parameterised route '/orders/{orderId}'*");
    }

    /// <summary>
    /// <see cref="EnumerateDeclaredRoutes"/> must return a non-empty,
    /// distinct, ordinally-sorted set. The sort makes the assertion in
    /// <see cref="ScannedRoutes_CoverEveryPublicPage"/> diff-friendly.
    /// </summary>
    [Fact]
    public void EnumerateDeclaredRoutes_ReturnsDistinctSortedSet()
    {
        var routes = EnumerateDeclaredRoutes();

        routes.Should().NotBeEmpty();
        routes.Should().OnlyHaveUniqueItems();
        routes.Should().BeInAscendingOrder(StringComparer.Ordinal);
        // Spot-check: the citizen portal landing page must always be present.
        routes.Should().Contain("/");
    }
}
