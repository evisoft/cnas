using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Infrastructure.Services.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — default <see cref="ISensitiveActionRegistry"/> implementation
/// backed by the DI-resolved <see cref="IEnumerable{T}"/> of
/// <see cref="ISensitiveActionPolicy"/> registrations. Materialises the policy set into
/// a stable ordered dictionary on construction so subsequent lookups are O(1) and the
/// <see cref="Describe"/> output is deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Registered Scoped because policies may themselves consume scoped
/// dependencies (e.g. a per-request DbContext) for cross-payload validation. The
/// substrate composes a fresh registry per request which is cheap — only the
/// already-resolved policy instances are enumerated.
/// </para>
/// </remarks>
public sealed class SensitiveActionRegistry : ISensitiveActionRegistry
{
    private readonly IReadOnlyDictionary<string, ISensitiveActionPolicy> _byCode;
    private readonly IReadOnlyList<SensitiveActionRegistryEntryDto> _descriptors;

    /// <summary>Constructs the registry from the DI-resolved policy set.</summary>
    /// <param name="policies">Every <see cref="ISensitiveActionPolicy"/> registered with the container.</param>
    public SensitiveActionRegistry(IEnumerable<ISensitiveActionPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var byCode = new Dictionary<string, ISensitiveActionPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            // Defence-in-depth — the last registration wins if two policies share a code,
            // but the architecture test ought to prevent that drift in the first place.
            byCode[policy.ActionCode] = policy;
        }
        _byCode = byCode;
        _descriptors = byCode
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SensitiveActionRegistryEntryDto(
                ActionCode: kv.Value.ActionCode,
                DisplayLabel: kv.Value.DisplayLabel,
                ExpirationHours: kv.Value.ExpirationOverride?.TotalHours))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SensitiveActionRegistryEntryDto> Describe() => _descriptors;

    /// <inheritdoc />
    public bool IsKnown(string actionCode)
        => !string.IsNullOrWhiteSpace(actionCode) && _byCode.ContainsKey(actionCode);

    /// <summary>
    /// Convenience accessor used by the service to obtain the policy for an action
    /// code. Returns <c>null</c> when no policy is registered.
    /// </summary>
    /// <param name="actionCode">Stable action code.</param>
    /// <returns>The matching policy, or <c>null</c>.</returns>
    internal ISensitiveActionPolicy? TryGetPolicy(string actionCode)
        => _byCode.TryGetValue(actionCode, out var policy) ? policy : null;
}
