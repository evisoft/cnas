using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting SIDDCM (disability records).
/// </summary>
/// <remarks>
/// Builds the <c>SIDDCM.GetDisabilityStatus</c> request, dispatches via MConnect, and
/// decodes the response into a nullable <see cref="SiddcmDisabilityRecord"/>. Upstream
/// returns either a JSON object representing the record, the literal <c>null</c>, or an
/// empty object <c>{}</c> for "no record on file"; all three are treated as
/// <c>Success(null)</c>.
/// </remarks>
public sealed class SiddcmClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<SiddcmClient> logger) : ISiddcmClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<SiddcmClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<SiddcmDisabilityRecord?>> GetDisabilityAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<SiddcmDisabilityRecord?>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }

        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("SIDDCM.GetDisabilityStatus", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<SiddcmDisabilityRecord?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (NullOrEmptyJson(raw.Value))
            {
                return Result<SiddcmDisabilityRecord?>.Success(null);
            }
            var record = JsonSerializer.Deserialize<SiddcmDisabilityRecord>(raw.Value, ExternalClientSerialization.Default);
            return Result<SiddcmDisabilityRecord?>.Success(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SIDDCM.GetDisabilityStatus returned a malformed JSON payload.");
            return Result<SiddcmDisabilityRecord?>.Failure(ErrorCodes.MConnectFailed, "SIDDCM returned a malformed JSON payload.");
        }
    }

    /// <summary>Returns true when the upstream response is the literal "null" or an empty object "{}".</summary>
    internal static bool NullOrEmptyJson(string raw)
    {
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.IsEmpty) return true;
        if (trimmed.SequenceEqual("null")) return true;
        if (trimmed.SequenceEqual("{}")) return true;
        return false;
    }
}
