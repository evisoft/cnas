using System.IO;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Structural checks for repository security ownership. These tests keep sensitive
/// paths from drifting back to implicit ownership after handover.
/// </summary>
public sealed class SecurityOwnershipTests
{
    private static readonly string[] SensitivePathPatterns =
    [
        "/src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs",
        "/src/Cnas.Ps.Api/Composition/AuthorizationComposition.cs",
        "/src/Cnas.Ps.Api/Security/",
        "/src/Cnas.Ps.Infrastructure/MGov/",
        "/src/Cnas.Ps.Infrastructure/Security/",
        "/src/Cnas.Ps.Infrastructure/Services/Audit/",
        "/src/Cnas.Ps.Infrastructure/Services/MNotify/",
        "/src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs",
        "/src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs",
        "/src/Cnas.Ps.Api/Controllers/MPassSamlController.cs",
        "/Directory.Packages.props",
        "/deploy/",
        "/ops/",
        "/.github/workflows/",
    ];

    [Fact]
    public void Codeowners_Exists()
    {
        File.Exists(Path.Combine(LocateRepoRoot(), ".github", "CODEOWNERS"))
            .Should()
            .BeTrue("security-sensitive code must have review ownership encoded in the repository");
    }

    [Fact]
    public void Codeowners_CoversSensitivePathsWithAtLeastTwoOwners()
    {
        var entries = LoadCodeownerEntries();

        foreach (var pattern in SensitivePathPatterns)
        {
            entries.Should().Contain(
                e => string.Equals(e.Pattern, pattern, StringComparison.Ordinal) && e.Owners.Length >= 2,
                $"sensitive path '{pattern}' must be owned by at least two teams or maintainers to avoid bus-factor-one review gates");
        }
    }

    private static (string Pattern, string[] Owners)[] LoadCodeownerEntries()
    {
        var path = Path.Combine(LocateRepoRoot(), ".github", "CODEOWNERS");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (parts[0], parts[1..]))
            .ToArray();
    }

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
            "Could not locate repository root from " + AppContext.BaseDirectory);
    }
}
