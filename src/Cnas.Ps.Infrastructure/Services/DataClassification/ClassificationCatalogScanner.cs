using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — production implementation of
/// <see cref="IClassificationCatalogScanner"/>. Scans every assembly whose
/// simple name starts with <c>Cnas.Ps.Contracts</c> (anchoring on the
/// loaded <see cref="SensitivityClassificationAttribute"/> assembly so the
/// scan is deterministic from process to process).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless + cacheable.</b> The scanner is registered as a singleton.
/// All reflection state is process-static so the per-fire allocation cost
/// is bounded by the property count of the Contracts assembly (~hundreds
/// of properties).
/// </para>
/// <para>
/// <b>Why we anchor on <see cref="SensitivityClassificationAttribute"/>.</b>
/// Looking up assemblies via <see cref="AppDomain.CurrentDomain"/> picks up
/// every loaded DLL — many of which are third-party libraries that have
/// nothing to do with CNAS classification policy. Anchoring on the
/// attribute's assembly lets us guarantee that we are scanning the
/// canonical Contracts assembly without depending on
/// <see cref="System.Reflection.Assembly.GetExecutingAssembly"/> patterns
/// that break in trimmed deployments.
/// </para>
/// </remarks>
public sealed class ClassificationCatalogScanner : IClassificationCatalogScanner
{
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyList<ScannedPropertyDto>> AssemblyCache = new();

    /// <summary>
    /// Performs one scan and returns the discovered properties + label
    /// counts. Determinism: the property list is sorted by
    /// <c>TypeFullName ASC, PropertyName ASC</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>The scan outcome.</returns>
    public Task<Result<ClassificationCatalogScanOutcomeDto>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assemblies = DiscoverContractsAssemblies();
        var allProperties = new List<ScannedPropertyDto>();
        var assemblyVersions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var typesScanned = 0;

        foreach (var assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assemblyName = assembly.GetName();
            var simpleName = assemblyName.Name ?? assembly.FullName ?? "(unknown)";
            assemblyVersions[simpleName] = assemblyName.Version?.ToString() ?? "0.0.0.0";

            var perAssembly = AssemblyCache.GetOrAdd(assembly, ScanAssembly);
            allProperties.AddRange(perAssembly);
            typesScanned += perAssembly.Select(p => p.TypeFullName).Distinct(StringComparer.Ordinal).Count();
        }

        // Final deterministic sort (in case multiple assemblies contributed).
        allProperties.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.TypeFullName, b.TypeFullName);
            return t != 0 ? t : string.CompareOrdinal(a.PropertyName, b.PropertyName);
        });

        var classified = allProperties.Count(p => p.IsExplicit);
        var unclassified = allProperties.Count - classified;

        var labelCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var labelName in Enum.GetNames<SensitivityLabel>())
        {
            labelCounts[labelName] = 0;
        }
        foreach (var prop in allProperties)
        {
            labelCounts.TryGetValue(prop.Label, out var existing);
            labelCounts[prop.Label] = existing + 1;
        }

        var outcome = new ClassificationCatalogScanOutcomeDto(
            TotalTypesScanned: typesScanned,
            TotalPropertiesClassified: classified,
            TotalPropertiesUnclassified: unclassified,
            Properties: allProperties,
            LabelCounts: labelCounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            AssemblyVersions: assemblyVersions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

        return Task.FromResult(Result<ClassificationCatalogScanOutcomeDto>.Success(outcome));
    }

    /// <summary>
    /// Discovers every loaded assembly whose simple name starts with
    /// <c>Cnas.Ps.Contracts</c>. The reference assembly (where the attribute
    /// lives) is always included even when no other Contracts assembly is
    /// loaded.
    /// </summary>
    /// <returns>The discovered assembly list (distinct, ordered by simple name).</returns>
    private static IReadOnlyList<Assembly> DiscoverContractsAssemblies()
    {
        var anchor = typeof(SensitivityClassificationAttribute).Assembly;
        var discovered = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        void TryAdd(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            if (!name.StartsWith("Cnas.Ps.Contracts", StringComparison.Ordinal))
            {
                return;
            }
            discovered[name] = assembly;
        }

        TryAdd(anchor);

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            TryAdd(loaded);
        }

        return discovered.Values
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Scans a single Contracts assembly: enumerates every public type,
    /// inspects each public instance property, and projects a
    /// <see cref="ScannedPropertyDto"/> with the resolved label + explicit
    /// flag.
    /// </summary>
    /// <param name="assembly">Assembly to scan.</param>
    /// <returns>The discovered properties in deterministic order.</returns>
    private static IReadOnlyList<ScannedPropertyDto> ScanAssembly(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var simpleName = assembly.GetName().Name ?? "(unknown)";
        var rows = new List<ScannedPropertyDto>();

        foreach (var type in types)
        {
            if (!type.IsPublic || type.IsCompilerGenerated())
            {
                continue;
            }

            var typeFloor = type.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);

            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is not null)
                {
                    continue;
                }
                if (property.Name.Contains('<', StringComparison.Ordinal)
                    || property.Name.Contains("__BackingField", StringComparison.Ordinal))
                {
                    continue;
                }

                var attribute = property.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);
                SensitivityLabel label;
                bool isExplicit;
                if (attribute is not null)
                {
                    label = attribute.Label;
                    isExplicit = true;
                }
                else if (typeFloor is not null)
                {
                    // R0228 — class-level floor counts as a (type-level) explicit
                    // declaration but registers IsExplicit=false on each property so
                    // operators still see which properties lack their own attribute.
                    label = typeFloor.Label;
                    isExplicit = false;
                }
                else
                {
                    label = SensitivityLabel.Internal;
                    isExplicit = false;
                }

                rows.Add(new ScannedPropertyDto(
                    TypeFullName: type.FullName ?? type.Name,
                    PropertyName: property.Name,
                    Label: label.ToString(),
                    IsExplicit: isExplicit,
                    DeclaringAssembly: simpleName));
            }
        }

        rows.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.TypeFullName, b.TypeFullName);
            return t != 0 ? t : string.CompareOrdinal(a.PropertyName, b.PropertyName);
        });

        return rows;
    }
}

/// <summary>
/// Internal reflection helpers used by the scanner. Mirrors the helper on
/// the architecture-test suite without taking a dependency on it.
/// </summary>
internal static class ScannerReflectionGuards
{
    /// <summary>True when the type was emitted by the compiler (lambdas, iterators, async state machines).</summary>
    /// <param name="type">Candidate type.</param>
    /// <returns><c>true</c> when the type carries a compiler-generated marker or has a <c>&lt;&gt;</c> name.</returns>
    public static bool IsCompilerGenerated(this Type type)
        => type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null
           || (type.Name.Contains('<', StringComparison.Ordinal) && type.Name.Contains('>', StringComparison.Ordinal));
}
