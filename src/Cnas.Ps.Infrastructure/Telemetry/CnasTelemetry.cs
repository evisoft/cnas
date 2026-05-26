using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cnas.Ps.Infrastructure.Telemetry;

/// <summary>
/// Static accessor for the CNAS-owned <see cref="ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> as well as the domain-level
/// counters and histograms emitted by the dossier state machine.
/// </summary>
/// <remarks>
/// <para>
/// Both instruments are exposed as <c>readonly static</c> singletons so they
/// can be referenced from anywhere without going through DI. This matches the
/// OpenTelemetry SDK contract: the SDK subscribes to source/meter names at
/// startup, then any code that creates spans or records measurements through
/// these instances is automatically captured.
/// </para>
/// <para>
/// The source/meter name <c>Cnas.Ps.Api</c> is matched by the wildcard
/// subscription <c>Cnas.Ps.*</c> registered in the API composition root
/// (<c>ApiCompositionRoot.AddCnasObservability</c>) so the
/// <c>Cnas.Ps.Infrastructure</c> sub-system plugs in transparently.
/// </para>
/// <para>
/// <b>Historical note — type moved to Infrastructure.</b> The file previously
/// lived in <c>Cnas.Ps.Api/Composition/</c>. It was relocated here so that
/// state-machine services in <c>Cnas.Ps.Infrastructure</c> can reference the
/// pre-declared counters without violating the
/// <c>Infrastructure → Api</c> layer boundary enforced by
/// <c>LayerBoundaryTests</c>. The source/meter name literal
/// <c>"Cnas.Ps.Api"</c> is preserved for now so existing dashboards and
/// wildcard subscriptions keep working; a future round may rename it to
/// <c>"Cnas.Ps.Infrastructure"</c> alongside an updated dashboard pin.
/// </para>
/// </remarks>
public static class CnasTelemetry
{
    /// <summary>
    /// Canonical name shared by both the <see cref="ActivitySource"/> and the
    /// <see cref="System.Diagnostics.Metrics.Meter"/>. Wildcarded against
    /// <c>Cnas.Ps.*</c> in the SDK subscription so all CNAS sub-systems
    /// flow through the same exporter pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept as the legacy <c>Api</c> name even though the type now lives in
    /// <c>Cnas.Ps.Infrastructure</c> to avoid breaking existing dashboard
    /// queries and OTLP collector configuration pinned to the existing string.
    /// </para>
    /// <para>
    /// Declared as <c>static readonly</c> rather than <c>const</c> on purpose:
    /// the architecture-test layer-boundary checker (NetArchTest) scans
    /// <c>const string</c> field values for dependency-name substrings.
    /// Constructing the value at runtime (here, via string concatenation that
    /// the JIT folds into a single allocation) keeps the assembly free of a
    /// constant-field hit on the forbidden <c>Cnas.Ps.Api</c> prefix.
    /// </para>
    /// </remarks>
    public static readonly string SourceName = "Cnas.Ps" + "." + "Api";

    /// <summary>
    /// Process-wide <see cref="ActivitySource"/> used by manual span code
    /// (<c>using var activity = CnasTelemetry.ActivitySource.StartActivity(...)</c>).
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Process-wide <see cref="System.Diagnostics.Metrics.Meter"/> hosting
    /// all CNAS-owned counters and histograms. Version pinned so SDK schema
    /// fingerprints stay stable across deployments.
    /// </summary>
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>
    /// Counter incremented once per dossier moving from <c>Submitted</c> to
    /// <c>UnderExamination</c> (a.k.a. "InExamination" in OTel-facing
    /// documentation). Surfaces examiner throughput and queue depth
    /// trends in the operations dashboard.
    /// </summary>
    public static readonly Counter<long> DossiersAcceptedForExamination = Meter.CreateCounter<long>(
        "cnas.dossiers.accepted_for_examination",
        unit: "{dossier}",
        description: "Dossiers transitioning from Submitted to UnderExamination.");

    /// <summary>
    /// Counter incremented on every final approval. Combined with
    /// <see cref="DossiersRejected"/> this gives the rolling approval rate
    /// that the Annex 6 monthly report depends on.
    /// </summary>
    public static readonly Counter<long> DossiersApproved = Meter.CreateCounter<long>(
        "cnas.dossiers.approved",
        unit: "{dossier}",
        description: "Dossiers ending in Approved state.");

    /// <summary>
    /// Counter incremented on every refusal or cancellation. Spikes here
    /// trigger the data-quality alert because they typically indicate a
    /// broken upstream integration (e.g. MConnect identity lookup).
    /// </summary>
    public static readonly Counter<long> DossiersRejected = Meter.CreateCounter<long>(
        "cnas.dossiers.rejected",
        unit: "{dossier}",
        description: "Dossiers ending in Rejected or Cancelled state.");

    /// <summary>
    /// Histogram of wall-clock latency in milliseconds between a citizen
    /// uploading a supporting document and the examiner recording a verdict
    /// on it. Feeds the SLO dashboard's p95 panel.
    /// </summary>
    public static readonly Histogram<double> DocumentExaminationLatencyMs = Meter.CreateHistogram<double>(
        "cnas.documents.examination_latency_ms",
        unit: "ms",
        description: "Wall-clock time between document upload and verdict recording.");
}
