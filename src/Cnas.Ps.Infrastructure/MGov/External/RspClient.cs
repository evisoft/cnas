using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting RSP (citizen civil register).
/// </summary>
/// <remarks>
/// Builds the <c>RSP.GetPerson</c> request body, dispatches via MConnect, deserialises the
/// camelCase response into <see cref="RspPerson"/>, and maps JSON failures to
/// <see cref="ErrorCodes.MConnectFailed"/>. The <c>asOfUtc</c> field on the wire is
/// generated from <see cref="ICnasTimeProvider.UtcNow"/> so that two requests issued at
/// the same logical time hash to the same MConnect correlation id (idempotency).
/// </remarks>
/// <param name="mconnect">Underlying MConnect transport.</param>
/// <param name="clock">UTC clock for the request stamp.</param>
/// <param name="logger">Structured logger; payloads carrying PII are never logged.</param>
public sealed class RspClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<RspClient> logger) : IRspClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<RspClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<RspPerson>> GetByIdnpAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<RspPerson>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }

        // RSP request body: {"idnp": "...", "asOfUtc": "..."} — second-precision UTC stamp
        // (matches MConnect canonical form, "yyyy-MM-ddTHH:mm:ssZ").
        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("RSP.GetPerson", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<RspPerson>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            var person = JsonSerializer.Deserialize<RspPerson>(raw.Value, ExternalClientSerialization.Default);
            if (person is null)
            {
                _logger.LogWarning("RSP.GetPerson returned an empty payload.");
                return Result<RspPerson>.Failure(ErrorCodes.MConnectFailed, "RSP returned an empty payload.");
            }
            return Result<RspPerson>.Success(person);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "RSP.GetPerson returned a malformed JSON payload.");
            return Result<RspPerson>.Failure(ErrorCodes.MConnectFailed, "RSP returned a malformed JSON payload.");
        }
    }
}
