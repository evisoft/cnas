using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// Immutable in-memory representation of a single <c>axe.run()</c> invocation. Mirrors
/// the four top-level arrays the JavaScript runtime returns: <c>violations</c>,
/// <c>passes</c>, <c>incomplete</c>, and <c>inapplicable</c>.
/// </summary>
/// <param name="Violations">Rules whose assertion was violated by one or more DOM nodes.</param>
/// <param name="Passes">Rules whose assertion was satisfied by every targeted node.</param>
/// <param name="Incomplete">Rules whose result could not be determined automatically and
/// require manual review.</param>
/// <param name="Inapplicable">Rules that did not match any DOM node (so neither pass
/// nor fail).</param>
public sealed record AxeAnalysisResult(
    IReadOnlyList<AxeViolation> Violations,
    IReadOnlyList<AxePass> Passes,
    IReadOnlyList<AxeIncomplete> Incomplete,
    IReadOnlyList<AxeInapplicable> Inapplicable)
{
    /// <summary>
    /// Shared serializer options. Property names in axe-core's JSON are camelCase;
    /// case-insensitive matching keeps the records' PascalCase parameter names compatible
    /// with the wire format without per-property attributes.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The set of axe-core impact strings the WCAG 2.1 AA gate fails on.
    /// </summary>
    /// <remarks>
    /// axe-core's documented vocabulary is <c>critical</c> | <c>serious</c> |
    /// <c>moderate</c> | <c>minor</c>; we treat the top two as build-blocking and the
    /// rest as logged warnings. Comparison is case-insensitive so a future axe-core
    /// release that capitalises differently does not silently bypass the gate.
    /// </remarks>
    private static readonly HashSet<string> BlockingImpacts =
        new(StringComparer.OrdinalIgnoreCase) { "critical", "serious" };

    /// <summary>
    /// Filters <see cref="Violations"/> down to the subset whose <see cref="AxeViolation.Impact"/>
    /// is <c>critical</c> or <c>serious</c> (case-insensitive). Unknown impact strings
    /// — anything not in axe-core's documented vocabulary — are treated as below the
    /// threshold and excluded from the result.
    /// </summary>
    /// <returns>
    /// A read-only list of build-blocking violations in the original order axe-core
    /// emitted them. The order is preserved so test output remains diff-friendly when
    /// the same scan runs twice on the same page.
    /// </returns>
    public IReadOnlyList<AxeViolation> SeriousOrCriticalViolations()
    {
        var blocking = new List<AxeViolation>(Violations.Count);
        foreach (var v in Violations)
        {
            if (v.Impact is not null && BlockingImpacts.Contains(v.Impact))
            {
                blocking.Add(v);
            }
        }
        return blocking;
    }

    /// <summary>
    /// Emits a single-line summary of the scan suitable for piping into
    /// <c>ITestOutputHelper.WriteLine</c> in the Playwright theory.
    /// </summary>
    /// <returns>
    /// <c>"violations=0"</c> when no violations are present, otherwise
    /// <c>"violations={total} (n critical, n serious, n moderate, n minor)"</c>. Empty
    /// per-impact buckets are still rendered so the format is stable across scans.
    /// </returns>
    public string ToShortReport()
    {
        if (Violations.Count == 0)
        {
            return "violations=0";
        }

        var critical = 0;
        var serious = 0;
        var moderate = 0;
        var minor = 0;
        foreach (var v in Violations)
        {
            var impact = v.Impact ?? string.Empty;
            if (string.Equals(impact, "critical", StringComparison.OrdinalIgnoreCase))
            {
                critical++;
            }
            else if (string.Equals(impact, "serious", StringComparison.OrdinalIgnoreCase))
            {
                serious++;
            }
            else if (string.Equals(impact, "moderate", StringComparison.OrdinalIgnoreCase))
            {
                moderate++;
            }
            else if (string.Equals(impact, "minor", StringComparison.OrdinalIgnoreCase))
            {
                minor++;
            }
            // Unknown impact strings are deliberately not counted in any bucket —
            // they still contribute to the total so the discrepancy is visible.
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "violations={0} ({1} critical, {2} serious, {3} moderate, {4} minor)",
            Violations.Count, critical, serious, moderate, minor);
    }

    /// <summary>
    /// Deserialises a raw axe-core JSON document into a <see cref="AxeAnalysisResult"/>.
    /// </summary>
    /// <param name="json">The string returned by <c>JSON.stringify(axe.run(...))</c>.</param>
    /// <returns>The populated result; missing arrays are normalised to empty lists.</returns>
    /// <exception cref="JsonException">Thrown if the input is not a valid JSON document
    /// or does not match the expected shape.</exception>
    public static AxeAnalysisResult FromJson(string json)
    {
        var parsed = JsonSerializer.Deserialize<AxeAnalysisResult>(json, JsonOptions)
            ?? throw new JsonException("axe-core returned a null JSON document.");
        return parsed with
        {
            Violations = parsed.Violations ?? [],
            Passes = parsed.Passes ?? [],
            Incomplete = parsed.Incomplete ?? [],
            Inapplicable = parsed.Inapplicable ?? [],
        };
    }
}

/// <summary>
/// A single axe-core rule violation — one entry of the <c>violations</c> array in the
/// raw scan output.
/// </summary>
/// <param name="Id">Stable rule id (e.g. <c>color-contrast</c>, <c>html-has-lang</c>).</param>
/// <param name="Impact">WCAG severity bucket: <c>critical</c> | <c>serious</c> |
/// <c>moderate</c> | <c>minor</c>. May be <c>null</c> when axe-core cannot determine
/// severity for the rule.</param>
/// <param name="Description">Human-readable description of what the rule asserts.</param>
/// <param name="Help">Short imperative help text suitable for an error log.</param>
/// <param name="HelpUrl">Permalink to the Deque University rule page.</param>
/// <param name="Nodes">DOM nodes that triggered the violation.</param>
public sealed record AxeViolation(
    string Id,
    string? Impact,
    string Description,
    string Help,
    string HelpUrl,
    IReadOnlyList<AxeNode> Nodes);

/// <summary>
/// A single axe-core rule pass — one entry of the <c>passes</c> array.
/// </summary>
/// <param name="Id">Stable rule id.</param>
/// <param name="Impact">Always <c>null</c> for passes in axe-core's output.</param>
/// <param name="Description">Human-readable description of what the rule asserts.</param>
/// <param name="Help">Short imperative help text.</param>
/// <param name="HelpUrl">Permalink to the Deque University rule page.</param>
/// <param name="Nodes">DOM nodes that satisfied the rule.</param>
public sealed record AxePass(
    string Id,
    string? Impact,
    string Description,
    string Help,
    string HelpUrl,
    IReadOnlyList<AxeNode> Nodes);

/// <summary>
/// A rule whose result could not be determined automatically.
/// </summary>
/// <param name="Id">Stable rule id.</param>
/// <param name="Impact">WCAG severity bucket if assignable; otherwise <c>null</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Help">Short imperative help text.</param>
/// <param name="HelpUrl">Permalink to the Deque University rule page.</param>
/// <param name="Nodes">DOM nodes flagged for manual review.</param>
public sealed record AxeIncomplete(
    string Id,
    string? Impact,
    string Description,
    string Help,
    string HelpUrl,
    IReadOnlyList<AxeNode> Nodes);

/// <summary>
/// A rule that did not match any DOM node on the scanned page.
/// </summary>
/// <param name="Id">Stable rule id.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Help">Short imperative help text.</param>
/// <param name="HelpUrl">Permalink to the Deque University rule page.</param>
public sealed record AxeInapplicable(
    string Id,
    string Description,
    string Help,
    string HelpUrl);

/// <summary>
/// A single DOM node referenced by an axe-core result entry.
/// </summary>
/// <param name="Target">CSS selectors locating the node (axe-core may return multiple
/// selectors when the node is inside an iframe).</param>
/// <param name="Html">Outer HTML of the node, truncated by axe-core if very large.</param>
/// <param name="FailureSummary">Human-readable explanation of how to fix the issue
/// (only populated on violation / incomplete entries).</param>
public sealed record AxeNode(
    IReadOnlyList<string> Target,
    string Html,
    string? FailureSummary);
