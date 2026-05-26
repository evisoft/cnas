using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Shared HTTP helpers for MGov clients. Centralises:
/// <list type="bullet">
///   <item>Deterministic correlation-id derivation (SHA-256 over the canonical JSON body).</item>
///   <item>Per-call header decoration (<c>Accept</c>, <c>Authorization</c>, <c>X-Correlation-Id</c>, <c>X-Request-Date</c>).</item>
///   <item>JSON serialisation options shared by every client.</item>
/// </list>
/// Kept <c>internal</c> because the contract is implementation detail of the MGov adapters.
/// </summary>
internal static class MGovHttp
{
    /// <summary>
    /// JSON options used for every outbound MGov payload. <c>camelCase</c> matches the
    /// upstream contract published by AGE; ignoring null values keeps wire bodies small
    /// and lets callers pass optional fields as <c>null</c>.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds a deterministic correlation id from <paramref name="canonicalPayload"/>. Two
    /// outbound calls with the same payload must produce the same id so MGov can dedupe
    /// retries (CLAUDE.md cross-cutting: Idempotent Callbacks). The first 16 hex chars of
    /// the SHA-256 digest are sufficient — collisions across distinct payloads are vanishingly
    /// improbable for the volumes CNAS handles, and the upstream side stores it as a string.
    /// </summary>
    /// <param name="canonicalPayload">Canonical JSON (or any byte-stable representation) of the request.</param>
    public static string DeriveCorrelationId(string canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        var bytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var hash = SHA256.HashData(bytes);
        // 16 hex chars == 8 bytes of the digest.
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decorates <paramref name="request"/> with the headers every MGov call requires:
    /// bearer authorization, accept-json, request-date and correlation id. Caller is
    /// responsible for the request body and the URI.
    /// </summary>
    /// <param name="request">Outbound message to decorate.</param>
    /// <param name="bearer">Static bearer token (may be empty in tests; passed verbatim).</param>
    /// <param name="correlationId">Deterministic correlation id from <see cref="DeriveCorrelationId(string)"/>.</param>
    /// <param name="clock">Time source — never <see cref="System.DateTime.UtcNow"/> directly.</param>
    public static void Decorate(
        HttpRequestMessage request,
        string bearer,
        string correlationId,
        ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clock);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        request.Headers.TryAddWithoutValidation("X-Request-Date", clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }
}
