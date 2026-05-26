using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting PCCM (medical certificates).
/// </summary>
/// <remarks>
/// Builds the <c>PCCM.GetMedicalCertificates</c> request with a UTC window, dispatches
/// via MConnect, and decodes the response into a list of <see cref="PccmCertificate"/>.
/// Tolerates both <c>{"certificates": [...]}</c> envelope and bare arrays.
/// </remarks>
public sealed class PccmClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<PccmClient> logger) : IPccmClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<PccmClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PccmCertificate>>> GetCertificatesAsync(string idnp, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<IReadOnlyList<PccmCertificate>>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }
        if (toUtc < fromUtc)
        {
            return Result<IReadOnlyList<PccmCertificate>>.Failure(ErrorCodes.InvalidDateRange, "toUtc must be >= fromUtc.");
        }

        var body = JsonSerializer.Serialize(
            new
            {
                idnp = idnpResult.Value.Value,
                fromUtc = fromUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                toUtc = toUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("PCCM.GetMedicalCertificates", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<IReadOnlyList<PccmCertificate>>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            JsonElement listElement = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("certificates", out var inner) => inner,
                _ => default,
            };

            if (listElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("PCCM.GetMedicalCertificates returned an unrecognised payload shape.");
                return Result<IReadOnlyList<PccmCertificate>>.Failure(ErrorCodes.MConnectFailed, "PCCM returned an unrecognised payload shape.");
            }

            var certs = listElement.Deserialize<List<PccmCertificate>>(ExternalClientSerialization.Default) ?? new List<PccmCertificate>();
            return Result<IReadOnlyList<PccmCertificate>>.Success(certs);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PCCM.GetMedicalCertificates returned a malformed JSON payload.");
            return Result<IReadOnlyList<PccmCertificate>>.Failure(ErrorCodes.MConnectFailed, "PCCM returned a malformed JSON payload.");
        }
    }
}
