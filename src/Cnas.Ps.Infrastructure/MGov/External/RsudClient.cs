using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting RSUD (legal-person register).
/// </summary>
/// <remarks>
/// Builds the <c>RSUD.GetLegalPerson</c> request body, dispatches via MConnect, decodes
/// camelCase JSON into <see cref="RsudLegalPerson"/>. JSON parse failures map to
/// <see cref="ErrorCodes.MConnectFailed"/>; IDNO validation failures bubble through with
/// <see cref="ErrorCodes.InvalidIdno"/>.
/// </remarks>
public sealed class RsudClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<RsudClient> logger) : IRsudClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<RsudClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<RsudLegalPerson>> GetByIdnoAsync(string idno, CancellationToken ct = default)
    {
        var idnoResult = Idno.TryCreate(idno);
        if (idnoResult.IsFailure)
        {
            return Result<RsudLegalPerson>.From(Result.Failure(idnoResult.ErrorCode!, idnoResult.ErrorMessage!));
        }

        var body = JsonSerializer.Serialize(
            new { idno = idnoResult.Value.Value, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("RSUD.GetLegalPerson", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<RsudLegalPerson>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            var entity = JsonSerializer.Deserialize<RsudLegalPerson>(raw.Value, ExternalClientSerialization.Default);
            if (entity is null)
            {
                _logger.LogWarning("RSUD.GetLegalPerson returned an empty payload.");
                return Result<RsudLegalPerson>.Failure(ErrorCodes.MConnectFailed, "RSUD returned an empty payload.");
            }
            return Result<RsudLegalPerson>.Success(entity);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "RSUD.GetLegalPerson returned a malformed JSON payload.");
            return Result<RsudLegalPerson>.Failure(ErrorCodes.MConnectFailed, "RSUD returned a malformed JSON payload.");
        }
    }
}
