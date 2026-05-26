using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting FMS (Treasury account state).
/// </summary>
/// <remarks>
/// Builds the <c>FMS.GetCnasAccountState</c> request, dispatches via MConnect, and decodes
/// the response into <see cref="FmsAccountState"/>. The lookup key is a CNAS-internal
/// treasury reference (assigned by CNAS, not by the citizen) and is therefore not
/// validated through <see cref="Cnas.Ps.Core.ValueObjects.Idnp"/> /
/// <see cref="Cnas.Ps.Core.ValueObjects.Idno"/>; we only require it be non-blank.
/// </remarks>
public sealed class FmsClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<FmsClient> logger) : IFmsClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<FmsClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<FmsAccountState>> GetAccountStateAsync(string cnasInternalRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cnasInternalRef))
        {
            return Result<FmsAccountState>.Failure(ErrorCodes.ValidationFailed, "cnasInternalRef must be non-blank.");
        }

        var body = JsonSerializer.Serialize(
            new { cnasInternalRef = cnasInternalRef.Trim(), asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("FMS.GetCnasAccountState", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<FmsAccountState>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            var state = JsonSerializer.Deserialize<FmsAccountState>(raw.Value, ExternalClientSerialization.Default);
            if (state is null)
            {
                _logger.LogWarning("FMS.GetCnasAccountState returned an empty payload.");
                return Result<FmsAccountState>.Failure(ErrorCodes.MConnectFailed, "FMS returned an empty payload.");
            }
            return Result<FmsAccountState>.Success(state);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FMS.GetCnasAccountState returned a malformed JSON payload.");
            return Result<FmsAccountState>.Failure(ErrorCodes.MConnectFailed, "FMS returned a malformed JSON payload.");
        }
    }
}
