using System.Text.Json;

namespace Cnas.Ps.E2E.Tests.Auth;

/// <summary>
/// Helper that serialises a <see cref="TestPrincipal"/> persona to the JSON literal
/// expected by <see cref="TestAuthHandler"/>. Cached <see cref="JsonSerializerOptions"/>
/// keeps CA1869 quiet — every journey test reuses the same instance instead of
/// constructing a new one per request.
/// </summary>
public static class TestPersonaHeader
{
    /// <summary>
    /// Cached web-defaults JSON serializer options (camel-case, case-insensitive).
    /// Shared across every header-build call site so the per-call allocation cost
    /// stays bounded and the analyzer (<c>CA1869</c>) is satisfied.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serialises the supplied <paramref name="persona"/> to a JSON string suitable
    /// for use on the <see cref="TestAuthHandler.HeaderName"/> request header.
    /// </summary>
    /// <param name="persona">Persona descriptor.</param>
    /// <returns>JSON literal carrying the persona fields (sub, roles, idnp, …).</returns>
    public static string Serialize(TestPrincipal persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        return JsonSerializer.Serialize(persona, Options);
    }
}
