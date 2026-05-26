using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — read-only registry of every registered
/// <see cref="ISensitiveActionPolicy"/>. Surfaces the known action codes to operator UI
/// (so the picker can render a list of allowed actions) and to the substrate (which
/// uses <see cref="IsKnown(string)"/> as a defence-in-depth check before persisting a
/// request).
/// </summary>
public interface ISensitiveActionRegistry
{
    /// <summary>
    /// Returns every registered policy as a DTO ordered by
    /// <see cref="SensitiveActionRegistryEntryDto.ActionCode"/>.
    /// </summary>
    /// <returns>The descriptor rows; empty when no policies are registered.</returns>
    IReadOnlyCollection<SensitiveActionRegistryEntryDto> Describe();

    /// <summary>True when at least one policy is registered for <paramref name="actionCode"/>.</summary>
    /// <param name="actionCode">Stable SCREAMING_SNAKE_CASE action code.</param>
    /// <returns><c>true</c> when a matching policy is registered; <c>false</c> otherwise.</returns>
    bool IsKnown(string actionCode);
}
