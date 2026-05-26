using System.Linq;
using Cnas.Ps.Infrastructure.Services.DataClassification;

namespace Cnas.Ps.Infrastructure.Tests.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — tests for
/// <see cref="ClassificationCatalogScanner"/>. Verifies that the scanner
/// loads, returns deterministic ordering, and surfaces explicit vs implicit
/// classification correctly against the live Contracts assembly.
/// </summary>
public sealed class ClassificationCatalogScannerTests
{
    [Fact]
    public async Task ScanAsync_DoesNotThrow_ReturnsSuccess()
    {
        var scanner = new ClassificationCatalogScanner();

        var result = await scanner.ScanAsync();

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ScanAsync_DiscoversAtLeastOneClassifiedProperty()
    {
        var scanner = new ClassificationCatalogScanner();

        var result = await scanner.ScanAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalPropertiesClassified.Should().BeGreaterThan(0);
        result.Value.Properties.Should().NotBeEmpty();
        result.Value.Properties.Should().Contain(p => p.IsExplicit);
    }

    [Fact]
    public async Task ScanAsync_PropertiesAreOrderedDeterministically()
    {
        var scanner = new ClassificationCatalogScanner();

        var result = await scanner.ScanAsync();

        result.IsSuccess.Should().BeTrue();
        var ordered = result.Value.Properties
            .OrderBy(p => p.TypeFullName, System.StringComparer.Ordinal)
            .ThenBy(p => p.PropertyName, System.StringComparer.Ordinal)
            .ToList();
        result.Value.Properties.Should().Equal(ordered);
    }

    [Fact]
    public async Task ScanAsync_LabelCountsIncludeEveryEnumName()
    {
        var scanner = new ClassificationCatalogScanner();

        var result = await scanner.ScanAsync();

        result.IsSuccess.Should().BeTrue();
        // Every SensitivityLabel enum name is preallocated even when the
        // count is zero so the dashboard chart never has missing buckets.
        result.Value.LabelCounts.Should().ContainKey("Public");
        result.Value.LabelCounts.Should().ContainKey("Internal");
        result.Value.LabelCounts.Should().ContainKey("Confidential");
        result.Value.LabelCounts.Should().ContainKey("Restricted");
    }

    [Fact]
    public async Task ScanAsync_AssemblyVersionsAreRestrictedToContractsPrefix()
    {
        var scanner = new ClassificationCatalogScanner();

        var result = await scanner.ScanAsync();

        result.IsSuccess.Should().BeTrue();
        // R2279 — reflection scope locked: ONLY assemblies starting with
        // "Cnas.Ps.Contracts" must appear in the assembly-versions map.
        result.Value.AssemblyVersions.Keys
            .Should().OnlyContain(name => name.StartsWith("Cnas.Ps.Contracts", System.StringComparison.Ordinal));
    }
}
