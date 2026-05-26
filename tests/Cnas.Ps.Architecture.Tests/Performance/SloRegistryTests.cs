using System.IO;
using Cnas.Ps.Core.Performance;

namespace Cnas.Ps.Architecture.Tests.Performance;

/// <summary>
/// Configuration-lock tests for the canonical SLO declaration
/// (TOR PSR 001 / PSR 010 — see <see cref="SloRegistry"/>).
///
/// <para>
/// Why these are architecture tests, not runtime tests:
/// </para>
/// <list type="bullet">
///   <item>
///     The numeric thresholds are <i>contractual</i>. They are not allowed to
///     drift silently — any change is a TOR amendment that must surface in
///     review. Locking the values here forces the change to be visible.
///   </item>
///   <item>
///     The k6 harness (<c>perf/cnas-baseline.js</c>) and the workflow
///     (<c>.github/workflows/perf-smoke.yml</c>) live outside the .NET build
///     graph. If a contributor accidentally deletes either, only an
///     architecture test can catch it. We therefore assert their presence on
///     disk via the repository-root locator pattern used by other tests in
///     this project (see <c>HuskyConfigurationTests</c>).
///   </item>
/// </list>
/// </summary>
public class SloRegistryTests
{
    /// <summary>
    /// k6 baseline script path, relative to the repository root.
    /// </summary>
    private const string K6ScriptRelativePath = "perf/cnas-baseline.js";

    /// <summary>
    /// CI workflow path, relative to the repository root.
    /// </summary>
    private const string PerfWorkflowRelativePath = ".github/workflows/perf-smoke.yml";

    [Fact]
    public void DefaultP90LatencyMs_Matches_PSR001_OneSecond_Target()
    {
        SloRegistry.DefaultP90LatencyMs.Should().Be(1000,
            "TOR PSR 001 declares p90 ≤ 1s for ordinary requests. " +
            "Changing this constant is a contract amendment and must surface in code review.");
    }

    [Fact]
    public void DefaultP99LatencyMs_Matches_PSR001_ThreeSecond_Target()
    {
        SloRegistry.DefaultP99LatencyMs.Should().Be(3000,
            "TOR PSR 001 declares p99 ≤ 3s for ordinary requests.");
    }

    [Fact]
    public void ReportP95LatencyMs_Matches_PSR001_FiveSecond_Target()
    {
        SloRegistry.ReportP95LatencyMs.Should().Be(5000,
            "TOR PSR 001 declares p95 ≤ 5s for report endpoints (CSV / XLSX / PDF exports).");
    }

    [Fact]
    public void DocumentOpP90LatencyMs_Matches_PSR010_ThreeSecond_Target()
    {
        SloRegistry.DocumentOpP90LatencyMs.Should().Be(3000,
            "TOR PSR 010 declares p90 ≤ 3s for document operations.");
    }

    [Fact]
    public void All_Returns_At_Least_Four_Latency_Entries()
    {
        var entries = SloRegistry.All();

        entries.Should().NotBeNull();
        entries.Count.Should().BeGreaterThanOrEqualTo(4,
            "alerting and dashboards iterate over SloRegistry.All() — dropping below " +
            "four latency SLOs would silently disable monitoring for one of the " +
            "PSR 001 / PSR 010 surfaces.");
    }

    [Fact]
    public void K6BaselineScript_Exists_At_PerfDirectory()
    {
        var path = Path.Combine(LocateRepoRoot(), K6ScriptRelativePath);

        File.Exists(path).Should().BeTrue(
            $"the k6 baseline harness '{K6ScriptRelativePath}' is the runtime carrier " +
            "for the SLO declarations. Without it the perf CI gate has nothing to run.");
    }

    [Fact]
    public void PerfSmokeWorkflow_Exists_At_WorkflowsDirectory()
    {
        var path = Path.Combine(LocateRepoRoot(), PerfWorkflowRelativePath);

        File.Exists(path).Should().BeTrue(
            $"the CI workflow '{PerfWorkflowRelativePath}' is what schedules k6 against " +
            "staging on every API-touching PR. A missing workflow turns the gate into a no-op.");
    }

    [Fact]
    public void K6BaselineScript_Declares_Default_P90_Latency_Threshold()
    {
        var path = Path.Combine(LocateRepoRoot(), K6ScriptRelativePath);
        var content = File.ReadAllText(path);

        // The literal threshold expression k6 understands: `p(90)<1000`.
        // We lock the string so a contributor relaxing the SLO in the script
        // (without touching SloRegistry.cs) cannot land that drift unnoticed.
        content.Should().Contain("p(90)<1000",
            "the k6 baseline must enforce the PSR 001 default p90 = 1000ms threshold. " +
            "Relaxing this string in the script is the most likely way to silently weaken " +
            "the SLO without changing SloRegistry.cs.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly's directory until it finds the repository root,
    /// identified by the presence of a <c>src/</c> sibling next to <c>tests/</c>.
    /// Mirrors the locator in <c>HuskyConfigurationTests</c> and
    /// <c>TimeProviderUsageTests</c>.
    /// </summary>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repository root (looked for sibling src/ and tests/ directories starting from " +
            $"{AppContext.BaseDirectory}).");
    }
}
