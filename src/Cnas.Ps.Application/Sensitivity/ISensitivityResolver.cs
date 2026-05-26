using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Application.Sensitivity;

/// <summary>
/// R0228 / TOR SEC 033 — resolver that reads
/// <see cref="SensitivityClassificationAttribute"/> annotations off DTO types and
/// returns the effective <see cref="SensitivityLabel"/> for the type or a specific
/// property. The infrastructure-side implementation caches per-type lookups so the
/// hot path on the response pipeline is allocation-free after the first request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default label.</b> When a property carries no attribute (and the declaring type
/// has no class-level annotation either), the resolver returns
/// <see cref="SensitivityLabel.Internal"/> — the safe floor described in the
/// <see cref="SensitivityLabel"/> remarks. This default never inflates the
/// <c>X-CNAS-Sensitivity</c> header above what the developer explicitly opted into.
/// </para>
/// <para>
/// <b>Type-level floor.</b> A class-level attribute behaves as a minimum: every
/// property reads at least the class label, regardless of its own annotation (or
/// lack thereof). Composition uses
/// <c>label = max(typeLabel, propertyLabel)</c>.
/// </para>
/// </remarks>
public interface ISensitivityResolver
{
    /// <summary>
    /// Returns the highest <see cref="SensitivityLabel"/> exposed by any property on
    /// <paramref name="type"/>, taking the class-level annotation as the floor. Used by
    /// the response middleware to populate <c>X-CNAS-Sensitivity</c>.
    /// </summary>
    /// <param name="type">Type to inspect (typically a DTO).</param>
    /// <returns>The effective label, never lower than <see cref="SensitivityLabel.Internal"/>.</returns>
    SensitivityLabel Resolve(Type type);

    /// <summary>
    /// Returns the effective <see cref="SensitivityLabel"/> for a single property.
    /// Falls back to the type-level label when the property carries none, and finally
    /// to <see cref="SensitivityLabel.Internal"/>.
    /// </summary>
    /// <param name="type">Declaring type.</param>
    /// <param name="propertyName">Property name to resolve.</param>
    /// <returns>The effective label.</returns>
    SensitivityLabel Resolve(Type type, string propertyName);

    /// <summary>
    /// Returns the per-property label map for <paramref name="type"/>. Implementations
    /// MUST return the same dictionary instance on repeat calls for the same type so the
    /// middleware can cache by reference.
    /// </summary>
    /// <param name="type">Type to inspect.</param>
    /// <returns>An immutable per-property label dictionary.</returns>
    IReadOnlyDictionary<string, SensitivityLabel> ResolveAll(Type type);
}
