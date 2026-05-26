using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting EESSI (EU cross-border
/// social security exchange).
/// </summary>
/// <remarks>
/// Builds the <c>EESSI.GetSocialSecurityRecord</c> request, dispatches via MConnect, and
/// decodes the response into a nullable <see cref="EessiRecord"/>. Lookup keys (member
/// state, foreign SSN) are domain primitives that do not pass through Moldovan
/// IDNP/IDNO validation — they originate in another jurisdiction.
/// </remarks>
public sealed class EessiClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<EessiClient> logger) : IEessiClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<EessiClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<EessiRecord?>> GetByForeignReferenceAsync(string memberStateCode, string foreignSsn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberStateCode) || memberStateCode.Trim().Length != 2)
        {
            return Result<EessiRecord?>.Failure(ErrorCodes.ValidationFailed, "memberStateCode must be a 2-letter ISO 3166 code.");
        }
        if (string.IsNullOrWhiteSpace(foreignSsn))
        {
            return Result<EessiRecord?>.Failure(ErrorCodes.ValidationFailed, "foreignSsn must be non-blank.");
        }

        var body = JsonSerializer.Serialize(
            new
            {
                memberStateCode = memberStateCode.Trim().ToUpperInvariant(),
                foreignSsn = foreignSsn.Trim(),
                asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("EESSI.GetSocialSecurityRecord", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<EessiRecord?>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            if (SiddcmClient.NullOrEmptyJson(raw.Value))
            {
                return Result<EessiRecord?>.Success(null);
            }
            var record = JsonSerializer.Deserialize<EessiRecord>(raw.Value, ExternalClientSerialization.Default);
            return Result<EessiRecord?>.Success(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "EESSI.GetSocialSecurityRecord returned a malformed JSON payload.");
            return Result<EessiRecord?>.Failure(ErrorCodes.MConnectFailed, "EESSI returned a malformed JSON payload.");
        }
    }
}
