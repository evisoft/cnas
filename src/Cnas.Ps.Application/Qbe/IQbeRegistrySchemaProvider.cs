namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — registry-schema lookup contract. Implementations expose a frozen,
/// startup-initialised mapping of registry-code → <see cref="QbeRegistrySchema"/> so the
/// converter can resolve a schema in O(1) per call without further allocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton in DI. The mapping is immutable after
/// startup; adding a new registry requires a code change (intentionally — the schema is a
/// security boundary).
/// </para>
/// <para>
/// <b>Unknown registries.</b> A lookup miss returns <see langword="null"/>; the converter
/// surfaces a stable <see cref="Cnas.Ps.Core.Common.ErrorCodes.QbeRegistryUnknown"/> failure
/// so the controller can return a precise 400 ProblemDetails instead of a generic error.
/// </para>
/// </remarks>
public interface IQbeRegistrySchemaProvider
{
    /// <summary>
    /// Returns the schema for <paramref name="registryCode"/>, or <see langword="null"/>
    /// when the registry is not registered.
    /// </summary>
    /// <param name="registryCode">Registry code; matched ordinal, case-sensitive.</param>
    /// <returns>The frozen <see cref="QbeRegistrySchema"/> or <see langword="null"/>.</returns>
    QbeRegistrySchema? GetForRegistry(string registryCode);
}
