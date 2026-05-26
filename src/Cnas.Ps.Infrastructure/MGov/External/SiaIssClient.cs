using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting SIAÎSȘ (unemployment register).
/// </summary>
/// <remarks>
/// Builds the <c>SIAISS.GetUnemploymentStatus</c> request, dispatches via MConnect, and
/// decodes the response into a nullable <see cref="SiaIssUnemploymentStatus"/>. "No
/// record" is conveyed as <c>null</c>/<c>{}</c>; both shapes map to <c>Success(null)</c>.
/// </remarks>
public sealed class SiaIssClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<SiaIssClient> logger) : ISiaIssClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<SiaIssClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<SiaIssUnemploymentStatus?>> GetUnemploymentAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<SiaIssUnemploymentStatus?>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }

        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("SIAISS.GetUnemploymentStatus", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<SiaIssUnemploymentStatus?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (SiddcmClient.NullOrEmptyJson(raw.Value))
            {
                return Result<SiaIssUnemploymentStatus?>.Success(null);
            }
            var record = JsonSerializer.Deserialize<SiaIssUnemploymentStatus>(raw.Value, ExternalClientSerialization.Default);
            return Result<SiaIssUnemploymentStatus?>.Success(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SIAISS.GetUnemploymentStatus returned a malformed JSON payload.");
            return Result<SiaIssUnemploymentStatus?>.Failure(ErrorCodes.MConnectFailed, "SIAÎSȘ returned a malformed JSON payload.");
        }
    }
}
