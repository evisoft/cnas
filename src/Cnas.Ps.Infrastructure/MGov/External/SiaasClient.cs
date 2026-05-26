using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting SIAAS (social assistance).
/// </summary>
/// <remarks>
/// Builds the <c>SIAAS.GetSocialAssistance</c> request, dispatches via MConnect, and
/// decodes the response into a nullable <see cref="SiaasRecord"/>.
/// </remarks>
public sealed class SiaasClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<SiaasClient> logger) : ISiaasClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<SiaasClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<SiaasRecord?>> GetAssistanceAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<SiaasRecord?>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }

        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("SIAAS.GetSocialAssistance", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<SiaasRecord?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (SiddcmClient.NullOrEmptyJson(raw.Value))
            {
                return Result<SiaasRecord?>.Success(null);
            }
            var record = JsonSerializer.Deserialize<SiaasRecord>(raw.Value, ExternalClientSerialization.Default);
            return Result<SiaasRecord?>.Success(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SIAAS.GetSocialAssistance returned a malformed JSON payload.");
            return Result<SiaasRecord?>.Failure(ErrorCodes.MConnectFailed, "SIAAS returned a malformed JSON payload.");
        }
    }
}
