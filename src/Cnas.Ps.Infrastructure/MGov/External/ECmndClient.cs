using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting eCMND (civil-status acts).
/// </summary>
/// <remarks>
/// Builds the <c>ECMND.GetCivilAct</c> request, dispatches via MConnect, and decodes the
/// response into a nullable <see cref="ECmndCivilAct"/>. The <c>actKind</c> argument is
/// validated against the closed set {BIRTH, DEATH, MARRIAGE, DIVORCE} — anything else is
/// rejected with <see cref="ErrorCodes.ValidationFailed"/>.
/// </remarks>
public sealed class ECmndClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<ECmndClient> logger) : IECmndClient
{
    private static readonly HashSet<string> AllowedActKinds = new(StringComparer.Ordinal)
    {
        "BIRTH", "DEATH", "MARRIAGE", "DIVORCE",
    };

    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<ECmndClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<ECmndCivilAct?>> GetCivilActAsync(string idnp, string actKind, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<ECmndCivilAct?>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }
        if (string.IsNullOrWhiteSpace(actKind) || !AllowedActKinds.Contains(actKind))
        {
            return Result<ECmndCivilAct?>.Failure(ErrorCodes.ValidationFailed, "actKind must be one of: BIRTH, DEATH, MARRIAGE, DIVORCE.");
        }

        var body = JsonSerializer.Serialize(
            new
            {
                idnp = idnpResult.Value.Value,
                actKind,
                asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("ECMND.GetCivilAct", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<ECmndCivilAct?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (SiddcmClient.NullOrEmptyJson(raw.Value))
            {
                return Result<ECmndCivilAct?>.Success(null);
            }
            var act = JsonSerializer.Deserialize<ECmndCivilAct>(raw.Value, ExternalClientSerialization.Default);
            return Result<ECmndCivilAct?>.Success(act);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ECMND.GetCivilAct returned a malformed JSON payload.");
            return Result<ECmndCivilAct?>.Failure(ErrorCodes.MConnectFailed, "eCMND returned a malformed JSON payload.");
        }
    }
}
