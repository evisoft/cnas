using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting SIVE (energy vulnerability).
/// </summary>
/// <remarks>
/// Builds the <c>SIVE.GetEnergyVulnerability</c> request, dispatches via MConnect, and
/// decodes the response into a nullable <see cref="SiveStatus"/>.
/// </remarks>
public sealed class SiveClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<SiveClient> logger) : ISiveClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<SiveClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<SiveStatus?>> GetVulnerabilityAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<SiveStatus?>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }

        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("SIVE.GetEnergyVulnerability", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<SiveStatus?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (SiddcmClient.NullOrEmptyJson(raw.Value))
            {
                return Result<SiveStatus?>.Success(null);
            }
            var record = JsonSerializer.Deserialize<SiveStatus>(raw.Value, ExternalClientSerialization.Default);
            return Result<SiveStatus?>.Success(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SIVE.GetEnergyVulnerability returned a malformed JSON payload.");
            return Result<SiveStatus?>.Failure(ErrorCodes.MConnectFailed, "SIVE returned a malformed JSON payload.");
        }
    }
}
