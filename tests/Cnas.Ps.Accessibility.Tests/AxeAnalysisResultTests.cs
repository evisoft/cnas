using System.Collections.Generic;
using System.Text.Json;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// Unit tests for <see cref="AxeAnalysisResult"/> — the immutable record set that
/// captures the JSON output of <c>axe.run()</c>. These tests do not require Playwright
/// or any browser; they verify the pure-data helpers that the Playwright theory uses
/// to triage scan output.
/// </summary>
/// <remarks>
/// <para>
/// These 10 tests are the ratchet that pushes the repo-wide test count up by R0223's
/// minimum even when the vendored <c>axe.min.js</c> placeholder is in place (so the
/// Playwright theory tests skip). They lock the contract between the runner and any
/// future report-generation consumer: filter, casing, order, and the canonical
/// short-report string.
/// </para>
/// </remarks>
public sealed class AxeAnalysisResultTests
{
    /// <summary>
    /// <see cref="AxeAnalysisResult.SeriousOrCriticalViolations"/> filters out the
    /// "moderate" and "minor" impacts so the assertion in the Playwright theory only
    /// flags genuine WCAG-blocking findings.
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_FiltersOutModerateAndMinor()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("color-contrast", "serious"),
            MakeViolation("region", "moderate"),
            MakeViolation("aria-roles", "minor"),
            MakeViolation("html-lang-valid", "critical"),
        };
        var result = MakeResult(violations);

        var filtered = result.SeriousOrCriticalViolations();

        filtered.Should().HaveCount(2);
        filtered.Select(v => v.Id).Should().BeEquivalentTo(["color-contrast", "html-lang-valid"]);
    }

    /// <summary>
    /// The filter preserves the order in which axe-core emitted the violations. The
    /// Playwright theory writes the list to <c>ITestOutputHelper</c> as part of the
    /// failure message; order stability keeps that output diff-friendly.
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_PreservesOriginalOrder()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("html-lang-valid", "critical"),
            MakeViolation("region", "moderate"),
            MakeViolation("color-contrast", "serious"),
            MakeViolation("aria-roles", "minor"),
            MakeViolation("button-name", "critical"),
        };
        var result = MakeResult(violations);

        var filtered = result.SeriousOrCriticalViolations();

        filtered.Select(v => v.Id).Should().Equal("html-lang-valid", "color-contrast", "button-name");
    }

    /// <summary>
    /// When every violation is below the serious-or-critical threshold the filter
    /// returns an empty list — the Playwright theory treats this as a passing scan.
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_ReturnsEmpty_WhenNoSeriousOrCritical()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("region", "moderate"),
            MakeViolation("aria-roles", "minor"),
        };
        var result = MakeResult(violations);

        result.SeriousOrCriticalViolations().Should().BeEmpty();
    }

    /// <summary>
    /// When every violation is at or above the threshold the filter returns the
    /// complete list (i.e. it does not over-filter or short-circuit).
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_ReturnsAll_WhenEveryViolationIsCritical()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("html-lang-valid", "critical"),
            MakeViolation("color-contrast", "critical"),
            MakeViolation("button-name", "critical"),
        };
        var result = MakeResult(violations);

        var filtered = result.SeriousOrCriticalViolations();

        filtered.Should().HaveCount(3);
        filtered.Select(v => v.Id).Should().Equal("html-lang-valid", "color-contrast", "button-name");
    }

    /// <summary>
    /// Round-trips a canonical axe-core JSON snippet through <see cref="AxeAnalysisResult.FromJson"/>.
    /// Locks the property names and nested-record shape the runner depends on.
    /// </summary>
    [Fact]
    public void FromJson_ParsesCanonicalAxeCoreOutput()
    {
        // The JSON shape is the documented axe-core 4.x output. Trimmed for brevity
        // but contains every field the runner reads.
        const string json = """
        {
            "violations": [
                {
                    "id": "color-contrast",
                    "impact": "serious",
                    "description": "Ensure foreground/background contrast meets WCAG AA.",
                    "help": "Elements must have sufficient color contrast",
                    "helpUrl": "https://dequeuniversity.com/rules/axe/4.10/color-contrast",
                    "nodes": [
                        {
                            "target": ["#login-btn"],
                            "html": "<button id=\"login-btn\">Login</button>",
                            "failureSummary": "Fix any of the following:\n  Element has insufficient color contrast of 2.5"
                        }
                    ]
                }
            ],
            "passes": [
                {
                    "id": "html-has-lang",
                    "impact": null,
                    "description": "Ensures every HTML document has a lang attribute",
                    "help": "<html> element must have a lang attribute",
                    "helpUrl": "https://dequeuniversity.com/rules/axe/4.10/html-has-lang",
                    "nodes": []
                }
            ],
            "incomplete": [],
            "inapplicable": []
        }
        """;

        var result = AxeAnalysisResult.FromJson(json);

        result.Violations.Should().HaveCount(1);
        var v = result.Violations[0];
        v.Id.Should().Be("color-contrast");
        v.Impact.Should().Be("serious");
        v.Help.Should().Be("Elements must have sufficient color contrast");
        v.HelpUrl.Should().Be("https://dequeuniversity.com/rules/axe/4.10/color-contrast");
        v.Nodes.Should().HaveCount(1);
        v.Nodes[0].Target.Should().ContainSingle().Which.Should().Be("#login-btn");
        v.Nodes[0].Html.Should().Contain("<button");

        result.Passes.Should().HaveCount(1);
        result.Passes[0].Id.Should().Be("html-has-lang");
        result.Incomplete.Should().BeEmpty();
        result.Inapplicable.Should().BeEmpty();
    }

    /// <summary>
    /// An unknown impact string ("trivial" — not in axe-core's vocabulary) must not
    /// crash the filter. Defines the contract: unknown impacts are treated as
    /// below-threshold and excluded from the serious-or-critical list.
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_TreatsUnknownImpactAsBelowThreshold()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("custom-rule", "trivial"),
            MakeViolation("color-contrast", "serious"),
        };
        var result = MakeResult(violations);

        var filtered = result.SeriousOrCriticalViolations();

        filtered.Should().ContainSingle().Which.Id.Should().Be("color-contrast");
    }

    /// <summary>
    /// An empty violations array must round-trip through the filter as an empty list
    /// (no <c>NullReferenceException</c> on lazy initialisation paths).
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_ReturnsEmpty_WhenViolationsArrayIsEmpty()
    {
        var result = MakeResult([]);

        result.SeriousOrCriticalViolations().Should().BeEmpty();
    }

    /// <summary>
    /// The filter is case-insensitive on the impact string — defensive against any
    /// future axe-core release that capitalises differently or against hand-written
    /// fixtures that use Title-case.
    /// </summary>
    [Fact]
    public void SeriousOrCriticalViolations_IsCaseInsensitive()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("rule-a", "Critical"),
            MakeViolation("rule-b", "SERIOUS"),
            MakeViolation("rule-c", "Moderate"),
        };
        var result = MakeResult(violations);

        var filtered = result.SeriousOrCriticalViolations();

        filtered.Select(v => v.Id).Should().BeEquivalentTo(["rule-a", "rule-b"]);
    }

    /// <summary>
    /// <see cref="AxeAnalysisResult.ToShortReport"/> emits the canonical one-line
    /// summary that the Playwright theory pipes into <c>ITestOutputHelper</c>.
    /// </summary>
    [Fact]
    public void ToShortReport_EmitsCanonicalOneLiner()
    {
        var violations = new List<AxeViolation>
        {
            MakeViolation("a", "critical"),
            MakeViolation("b", "serious"),
            MakeViolation("c", "serious"),
            MakeViolation("d", "moderate"),
            MakeViolation("e", "moderate"),
            MakeViolation("f", "minor"),
        };
        var result = MakeResult(violations);

        result.ToShortReport()
            .Should().Be("violations=6 (1 critical, 2 serious, 2 moderate, 1 minor)");
    }

    /// <summary>
    /// <see cref="AxeAnalysisResult.ToShortReport"/> on a clean scan emits the
    /// "violations=0" canonical form (no parens, no per-bucket noise).
    /// </summary>
    [Fact]
    public void ToShortReport_ZeroViolations_EmitsZeroOnly()
    {
        var result = MakeResult([]);

        result.ToShortReport().Should().Be("violations=0");
    }

    // --- helpers --------------------------------------------------------------

    /// <summary>
    /// Builds a violation with sensible defaults so individual tests can focus on the
    /// one property they exercise (Id + Impact). Description/Help/HelpUrl/Nodes are
    /// canned strings — none of the assertions in this file depend on their content.
    /// </summary>
    private static AxeViolation MakeViolation(string id, string impact) => new(
        Id: id,
        Impact: impact,
        Description: $"Description for {id}.",
        Help: $"Help for {id}.",
        HelpUrl: $"https://dequeuniversity.com/rules/axe/{id}",
        Nodes: []);

    /// <summary>
    /// Builds a result populated only with the supplied violations — passes /
    /// incomplete / inapplicable are empty.
    /// </summary>
    private static AxeAnalysisResult MakeResult(IReadOnlyList<AxeViolation> violations) => new(
        Violations: violations,
        Passes: [],
        Incomplete: [],
        Inapplicable: []);
}
