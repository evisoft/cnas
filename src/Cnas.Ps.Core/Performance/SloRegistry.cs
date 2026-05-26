namespace Cnas.Ps.Core.Performance;

/// <summary>
/// Canonical Service-Level Objective (SLO) declarations for SI „Protecția Socială".
///
/// <para>
/// The values below codify the contractual performance targets from TOR
/// <c>PSR 001</c> (Performance &amp; Scalability — Response Times) and
/// <c>PSR 010</c> (Report &amp; Document-Operation Targets). They are referenced by:
/// </para>
/// <list type="bullet">
///   <item>The k6 baseline harness at <c>perf/cnas-baseline.js</c> (thresholds).</item>
///   <item>Future alerting + dashboards (Prometheus rules — separate batch).</item>
///   <item>Architecture tests that lock the thresholds against accidental drift.</item>
/// </list>
///
/// <para>
/// <b>Why constants rather than configuration:</b> the SLOs are contractual values
/// declared in the TOR. Changing them is a contract amendment, not a runtime tweak.
/// Encoding them in code (and locking them via architecture tests) makes every change
/// observable in code review.
/// </para>
///
/// <para>
/// <b>Where these values come from:</b>
/// </para>
/// <list type="bullet">
///   <item><c>DefaultP90LatencyMs</c> / <c>DefaultP99LatencyMs</c> — PSR 001 regular requests.</item>
///   <item><c>ReportP95LatencyMs</c> — PSR 001 reporting endpoints.</item>
///   <item><c>DocumentOpP90LatencyMs</c> / <c>DocumentOpP99LatencyMs</c> — PSR 010 document operations.</item>
///   <item><c>ConcurrentAuthorizedTarget</c> — PSR 002.</item>
///   <item><c>ConcurrentAnonymousTarget</c> — PSR 002 (anonymous lane).</item>
///   <item><c>ConcurrentSessionsTarget</c> — PSR 003.</item>
///   <item><c>DailyTransactionTarget</c> — PSR 005 / volumetric expectations.</item>
/// </list>
/// </summary>
/// <example>
/// Read the canonical p90 threshold:
/// <code>
/// var threshold = SloRegistry.DefaultP90LatencyMs; // 1000
/// </code>
/// Enumerate every declared SLO (for example to render a dashboard):
/// <code>
/// foreach (var slo in SloRegistry.All())
/// {
///     Console.WriteLine($"{slo.Code}: {slo.Percentile} &lt;= {slo.ThresholdMs}ms");
/// }
/// </code>
/// </example>
public static class SloRegistry
{
    // ---------------------------------------------------------------------
    // Latency thresholds (milliseconds)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Default p90 latency target for ordinary (non-report, non-document) requests.
    /// <para>Source: TOR PSR 001 — "90% of requests complete within 1 second".</para>
    /// </summary>
    public const double DefaultP90LatencyMs = 1000;

    /// <summary>
    /// Default p99 latency target for ordinary requests.
    /// <para>Source: TOR PSR 001 — "99% of requests complete within 3 seconds".</para>
    /// </summary>
    public const double DefaultP99LatencyMs = 3000;

    /// <summary>
    /// p95 latency target for report-generating endpoints (CSV / XLSX / PDF exports
    /// from grids and aggregated views).
    /// <para>Source: TOR PSR 001 — "report endpoints, 95% within 5 seconds".</para>
    /// </summary>
    public const double ReportP95LatencyMs = 5000;

    /// <summary>
    /// p90 latency target for document operations (rendering, signing handoff,
    /// archiving) per TOR PSR 010.
    /// </summary>
    public const double DocumentOpP90LatencyMs = 3000;

    /// <summary>
    /// p99 latency target for document operations — long tail allowance for
    /// signed-PDF rendering and large attachments.
    /// <para>Source: TOR PSR 010.</para>
    /// </summary>
    public const double DocumentOpP99LatencyMs = 8000;

    // ---------------------------------------------------------------------
    // Concurrency targets
    // ---------------------------------------------------------------------

    /// <summary>
    /// Target concurrent authorised (logged-in) users the system must sustain.
    /// <para>Source: TOR PSR 002.</para>
    /// </summary>
    public const int ConcurrentAuthorizedTarget = 1500;

    /// <summary>
    /// Target concurrent anonymous (public catalogue / help portal) users.
    /// <para>Source: TOR PSR 002.</para>
    /// </summary>
    public const int ConcurrentAnonymousTarget = 500;

    /// <summary>
    /// Target concurrent active sessions across all surfaces.
    /// <para>Source: TOR PSR 003.</para>
    /// </summary>
    public const int ConcurrentSessionsTarget = 2000;

    // ---------------------------------------------------------------------
    // Volumetrics
    // ---------------------------------------------------------------------

    /// <summary>
    /// Daily transaction-volume target the system must handle without degradation.
    /// <para>Source: TOR PSR 005 / volumetric model in TOR §2.7.</para>
    /// </summary>
    public const long DailyTransactionTarget = 300_000;

    // ---------------------------------------------------------------------
    // Enumeration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns the canonical list of latency SLOs that downstream systems
    /// (alerting, dashboards, k6 harness) should treat as a single source of truth.
    /// <para>
    /// Concurrency / volumetric targets are exposed as constants only — they are
    /// not "latency SLOs" and therefore do not appear in this list.
    /// </para>
    /// </summary>
    /// <returns>
    /// An immutable list of <see cref="SloEntry"/> records, one per declared
    /// latency SLO, with stable codes suitable for Prometheus / Grafana labels.
    /// </returns>
    public static IReadOnlyList<SloEntry> All() => Latencies;

    private static readonly IReadOnlyList<SloEntry> Latencies =
    [
        new SloEntry(
            Code: "PSR_001_DEFAULT_P90",
            Description: "Default p90 latency target for ordinary requests (PSR 001).",
            ThresholdMs: DefaultP90LatencyMs,
            Percentile: "p90"),
        new SloEntry(
            Code: "PSR_001_DEFAULT_P99",
            Description: "Default p99 latency target for ordinary requests (PSR 001).",
            ThresholdMs: DefaultP99LatencyMs,
            Percentile: "p99"),
        new SloEntry(
            Code: "PSR_001_REPORT_P95",
            Description: "Report endpoints p95 latency target (PSR 001).",
            ThresholdMs: ReportP95LatencyMs,
            Percentile: "p95"),
        new SloEntry(
            Code: "PSR_010_DOC_P90",
            Description: "Document operations p90 latency target (PSR 010).",
            ThresholdMs: DocumentOpP90LatencyMs,
            Percentile: "p90"),
        new SloEntry(
            Code: "PSR_010_DOC_P99",
            Description: "Document operations p99 latency target (PSR 010).",
            ThresholdMs: DocumentOpP99LatencyMs,
            Percentile: "p99"),
    ];
}

/// <summary>
/// A single Service-Level Objective entry.
/// </summary>
/// <param name="Code">
/// Stable, machine-readable identifier (SCREAMING_SNAKE_CASE) suitable for
/// Prometheus labels and alert-rule names. Treat as part of the public contract;
/// renaming is a breaking change.
/// </param>
/// <param name="Description">
/// Human-readable summary of what the SLO covers, including the TOR clause it
/// derives from.
/// </param>
/// <param name="ThresholdMs">
/// Maximum acceptable latency in milliseconds for the named percentile.
/// </param>
/// <param name="Percentile">
/// Latency percentile name (<c>"p90"</c>, <c>"p95"</c>, <c>"p99"</c>) the threshold applies to.
/// </param>
/// <example>
/// <code>
/// var p90 = new SloEntry("PSR_001_DEFAULT_P90", "Default p90", 1000, "p90");
/// </code>
/// </example>
public sealed record SloEntry(string Code, string Description, double ThresholdMs, string Percentile);
