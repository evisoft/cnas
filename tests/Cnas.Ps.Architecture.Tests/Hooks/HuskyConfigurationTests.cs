using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cnas.Ps.Architecture.Tests.Hooks;

/// <summary>
/// Configuration assertions for the local pre-commit chain (CLAUDE.md §1.6, TODO.md R0005).
///
/// Why this is an architecture test, not a runtime test:
///   * The pre-commit chain is a structural rule: every gate listed in CLAUDE.md §1.6
///     (format → build → test) MUST exist in both task-runner.json and the shell wrapper,
///     in the exact group, with the exact build flag.
///   * If a contributor accidentally deletes a gate, removes the
///     <c>TreatWarningsAsErrors=true</c> flag, or unsubscribes a task from the
///     <c>pre-commit</c> group, the local chain silently degrades and CI becomes the only
///     enforcement. That's a Day-1 quality regression we catch here.
///
/// The tests inspect the JSON / shell files on disk via the repository root, exactly the
/// same way <c>TimeProviderUsageTests</c> and <c>NamingConventionTests</c> do — no runtime
/// invocation of <c>dotnet husky</c> is performed (that would slow the suite and depend on
/// the contributor's tool restore state).
/// </summary>
public class HuskyConfigurationTests
{
    /// <summary>
    /// Husky.Net task-runner manifest. Relative to the repository root.
    /// </summary>
    private const string TaskRunnerJsonRelativePath = ".husky/task-runner.json";

    /// <summary>
    /// Shell wrapper Git invokes on <c>pre-commit</c>. Relative to the repository root.
    /// </summary>
    private const string PreCommitHookRelativePath = ".husky/pre-commit";

    /// <summary>
    /// Local .NET tool manifest — Husky is registered here as a local tool so
    /// <c>dotnet tool restore</c> on a fresh clone provides it without a global install.
    /// </summary>
    private const string ToolManifestRelativePath = ".config/dotnet-tools.json";

    [Fact]
    public void TaskRunnerJson_Exists_At_HuskyDirectory()
    {
        var path = Path.Combine(LocateRepoRoot(), TaskRunnerJsonRelativePath);

        File.Exists(path).Should().BeTrue(
            $"Husky.Net reads tasks from '{TaskRunnerJsonRelativePath}'. Without it the " +
            "pre-commit wrapper has nothing to run and the 3-gate chain silently turns into a no-op.");
    }

    [Fact]
    public void TaskRunnerJson_Defines_Three_PreCommit_Tasks_Named_Format_Build_And_Test()
    {
        var taskNames = LoadTaskNames();

        taskNames.Should().Contain("format-staged-cs",
            "the format gate (CLAUDE.md §1.6 Gate 1) is the first defence against style drift");
        taskNames.Should().Contain("build-warnings-as-errors",
            "the build gate (CLAUDE.md §1.6 Gate 2) enforces TreatWarningsAsErrors locally, " +
            "mirroring Directory.Build.props");
        taskNames.Should().Contain("run-tests",
            "the test gate (CLAUDE.md §1.6 Gate 3) catches regressions before they reach CI");
    }

    [Fact]
    public void TaskRunnerJson_AllThreeGates_Belong_To_PreCommitGroup()
    {
        var tasks = LoadTasks();

        // The wrapper invokes `dotnet husky run --group pre-commit`. Any gate task that
        // is not in this group is silently skipped — exactly the failure mode this test guards.
        var gateNames = new[] { "format-staged-cs", "build-warnings-as-errors", "run-tests" };
        foreach (var name in gateNames)
        {
            var task = tasks.First(t => t.GetProperty("name").GetString() == name);
            task.TryGetProperty("group", out var groupProp).Should().BeTrue(
                $"task '{name}' must declare a 'group' property — otherwise `husky run --group pre-commit` skips it.");
            groupProp.GetString().Should().Be("pre-commit",
                $"task '{name}' must be in the 'pre-commit' group to participate in the local chain.");
        }
    }

    [Fact]
    public void FormatTask_Targets_CSharp_Sources()
    {
        var tasks = LoadTasks();
        var format = tasks.First(t => t.GetProperty("name").GetString() == "format-staged-cs");

        format.TryGetProperty("include", out var includeProp).Should().BeTrue(
            "the format task must scope itself to .cs files via 'include' — running it on every staged file is wasteful and can break on binary diffs.");
        var includes = includeProp.EnumerateArray().Select(e => e.GetString()).ToArray();
        includes.Should().Contain("**/*.cs",
            "the format gate must cover all C# sources in the repository, not just a subset.");
    }

    [Fact]
    public void BuildTask_Passes_TreatWarningsAsErrors_Flag()
    {
        var tasks = LoadTasks();
        var build = tasks.First(t => t.GetProperty("name").GetString() == "build-warnings-as-errors");
        var args = build.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToArray();

        args.Should().Contain("-p:TreatWarningsAsErrors=true",
            "even though Directory.Build.props sets the property, the explicit flag here is " +
            "defensive against a local .csproj override and documents intent at the gate.");
        args.Should().Contain("Cnas.Ps.slnx",
            "the build gate must target the solution file so every project is verified, " +
            "not just whichever project was last touched.");
    }

    [Fact]
    public void DotnetToolManifest_Registers_Husky_As_Local_Tool()
    {
        var path = Path.Combine(LocateRepoRoot(), ToolManifestRelativePath);
        File.Exists(path).Should().BeTrue(
            $"the local tool manifest '{ToolManifestRelativePath}' is the source of truth for " +
            "`dotnet tool restore`. Without it, Husky.Net is not available on fresh clones.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var tools = doc.RootElement.GetProperty("tools");
        tools.TryGetProperty("husky", out var husky).Should().BeTrue(
            "Husky.Net must be declared in the tool manifest so the auto-install MSBuild target can call `dotnet husky install`.");

        var commands = husky.GetProperty("commands").EnumerateArray().Select(e => e.GetString()).ToArray();
        commands.Should().Contain("husky",
            "the 'husky' command alias must be present for the pre-commit wrapper's `dotnet husky run` invocation.");
    }

    [Fact]
    public void PreCommitHook_Exists_And_Delegates_To_HuskyRun_PreCommitGroup()
    {
        var path = Path.Combine(LocateRepoRoot(), PreCommitHookRelativePath);
        File.Exists(path).Should().BeTrue(
            $"the shell wrapper '{PreCommitHookRelativePath}' is what Git actually executes on commit — " +
            "task-runner.json alone is configuration without an entry point.");

        var content = File.ReadAllText(path);
        content.Should().Contain("husky run",
            "the wrapper must delegate to `dotnet husky run` rather than re-implementing gates inline; " +
            "duplicating the gate definitions in two places leads to drift between the shell script and task-runner.json.");
        content.Should().Contain("--group pre-commit",
            "the wrapper must scope execution to the 'pre-commit' group so other Husky groups (e.g. pre-push) don't run on every commit.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly's directory until it finds the repository root,
    /// identified by the presence of a <c>src/</c> sibling next to <c>tests/</c>.
    /// Mirrors the locator in <see cref="TimeProviderUsageTests"/>.
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

    /// <summary>
    /// Loads <c>.husky/task-runner.json</c> and returns the <c>tasks</c> array as a list
    /// of <see cref="JsonElement"/>. Throws if the file is missing — callers should
    /// guard via the dedicated existence test (<see cref="TaskRunnerJson_Exists_At_HuskyDirectory"/>)
    /// to surface that root cause first.
    /// </summary>
    private static JsonElement[] LoadTasks()
    {
        var path = Path.Combine(LocateRepoRoot(), TaskRunnerJsonRelativePath);
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        // Clone each element so they survive disposal of the JsonDocument.
        return doc.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(e => e.Clone()).ToArray();
    }

    /// <summary>Convenience: project the <c>name</c> property out of every task.</summary>
    private static string?[] LoadTaskNames() =>
        LoadTasks().Select(t => t.GetProperty("name").GetString()).ToArray();
}
