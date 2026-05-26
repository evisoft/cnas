using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// R0228 / TOR SEC 033 — reflection-based <see cref="ISensitivityResolver"/>
/// implementation. Caches a per-type
/// <c>(typeLabel, per-property labels)</c> snapshot in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> so subsequent resolves are
/// constant-time and allocation-free.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read pattern.</b> The middleware on the response hot-path calls
/// <see cref="ResolveAll"/> once per response — that call is cached after the first
/// hit for a given type. The cache key is the <see cref="Type"/> reference itself, so
/// the dictionary's hashing is identity-based and never compares strings.
/// </para>
/// <para>
/// <b>Floor composition.</b> Every property in the returned map reads
/// <c>max(typeLabel, propertyLabel)</c> exactly as documented on
/// <see cref="ISensitivityResolver"/>. Properties without their own attribute simply
/// inherit the type label (or <see cref="SensitivityLabel.Internal"/> if none).
/// </para>
/// <para>
/// <b>Inheritance.</b> The type-level lookup uses <c>inherit: true</c> so derived DTOs
/// pick up the base class's floor without re-annotating.
/// </para>
/// </remarks>
public sealed class SensitivityResolver : ISensitivityResolver
{
    /// <summary>Per-type snapshot built on first resolve.</summary>
    private sealed record TypeSnapshot(
        SensitivityLabel TypeLabel,
        IReadOnlyDictionary<string, SensitivityLabel> Properties,
        SensitivityLabel Highest);

    /// <summary>
    /// Process-wide identity-keyed cache. Reads are lock-free; the
    /// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
    /// race is acceptable because the snapshot factory is idempotent.
    /// </summary>
    private readonly ConcurrentDictionary<Type, TypeSnapshot> _cache = new();

    /// <inheritdoc />
    public SensitivityLabel Resolve(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return GetSnapshot(type).Highest;
    }

    /// <inheritdoc />
    public SensitivityLabel Resolve(Type type, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        var snap = GetSnapshot(type);

        // Per-property lookup. Missing property → fall back to the type floor (which is
        // already at least Internal). This matches the contract documented on
        // ISensitivityResolver: property > type > Internal.
        if (snap.Properties.TryGetValue(propertyName, out var label))
        {
            return label;
        }

        return snap.TypeLabel;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, SensitivityLabel> ResolveAll(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return GetSnapshot(type).Properties;
    }

    /// <summary>
    /// Builds or returns the cached snapshot for <paramref name="type"/>. The factory is
    /// idempotent so the rare race during GetOrAdd is harmless.
    /// </summary>
    /// <param name="type">DTO type to inspect.</param>
    /// <returns>The cached snapshot.</returns>
    private TypeSnapshot GetSnapshot(Type type)
        => _cache.GetOrAdd(type, BuildSnapshot);

    /// <summary>
    /// Reflects over <paramref name="type"/>'s public instance properties, composing
    /// the per-property labels with the type-level floor.
    /// </summary>
    /// <param name="type">DTO type to inspect.</param>
    /// <returns>A frozen snapshot ready for cache insertion.</returns>
    private static TypeSnapshot BuildSnapshot(Type type)
    {
        // The class-level attribute is the EXPLICIT floor. When absent it is NOT treated
        // as a floor of Internal — that would clobber properties explicitly marked Public.
        // The Internal default kicks in only when BOTH the property AND the type carry no
        // annotation at all (covered by the per-property branch below).
        var typeAttr = type.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);
        var typeLabel = typeAttr?.Label;

        // For Resolve(Type) callers, `effectiveTypeLabel` is what we report when no
        // properties are annotated. Internal is the documented default.
        var effectiveTypeLabel = typeLabel ?? SensitivityLabel.Internal;

        var map = new Dictionary<string, SensitivityLabel>(StringComparer.Ordinal);

        // Seed `highest` with the type-level floor when one is explicitly set, otherwise
        // start at Public so a DTO whose properties are all explicitly Public reports as
        // Public (not the Internal default). When there are no properties at all, we
        // fall back to `effectiveTypeLabel` below.
        var highest = typeLabel ?? SensitivityLabel.Public;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propAttr = prop.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);

            // Resolution order:
            //   1. Property attribute (if present) — caller is explicit.
            //   2. Class attribute (if present) — caller set a floor at the type level.
            //   3. Internal default — the documented safe floor.
            // Then enforce the type-level floor: if the type carries an attribute the
            // property reading must never drop below it.
            SensitivityLabel propLabel = propAttr?.Label
                ?? typeLabel
                ?? SensitivityLabel.Internal;

            if (typeLabel is { } floor && propLabel < floor)
            {
                propLabel = floor;
            }

            map[prop.Name] = propLabel;

            if (propLabel > highest)
            {
                highest = propLabel;
            }
        }

        // Empty-DTO edge case: an unannotated type with zero public properties leaves
        // `highest` at the seeded Public, but the documented "Resolve(Type) returns at
        // least Internal" contract still applies. Bump to the effective type label.
        if (map.Count == 0 && highest < effectiveTypeLabel)
        {
            highest = effectiveTypeLabel;
        }

        // Wrap in a ReadOnlyDictionary so the cached reference is immutable AND every
        // subsequent ResolveAll(...) call returns the SAME instance (identity equality
        // required by the test contract).
        var readOnly = new ReadOnlyDictionary<string, SensitivityLabel>(map);
        return new TypeSnapshot(effectiveTypeLabel, readOnly, highest);
    }
}
