using System.Text.Json;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> used by every typed MConnect facade client.
/// </summary>
/// <remarks>
/// MConnect-routed services return JSON in camelCase (the MGov platform convention).
/// We centralise the options here so every facade decodes payloads consistently and so
/// the (very small) set of decisions — case-insensitivity, camelCase naming — is in one
/// place if a future MConnect schema change forces a tweak.
/// </remarks>
internal static class ExternalClientSerialization
{
    /// <summary>
    /// JSON serialization defaults for typed facades over MConnect.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
