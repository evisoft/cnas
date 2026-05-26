using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov.External;

/// <summary>
/// Typed facade over <see cref="IMConnectClient"/> targeting SFS (salary declarations).
/// </summary>
/// <remarks>
/// Builds the <c>SFS.GetSalaryDeclarations</c> request, dispatches via MConnect, decodes
/// the response into a list of <see cref="SfsDeclaration"/>. The wire shape is
/// <c>{"declarations": [...]}</c>; we tolerate both that envelope and a raw top-level
/// array (the upstream documentation lists both as valid).
/// </remarks>
public sealed class SfsClient(
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<SfsClient> logger) : ISfsClient
{
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<SfsClient> _logger = logger;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SfsDeclaration>>> GetDeclarationsAsync(string idnp, int year, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<IReadOnlyList<SfsDeclaration>>.From(Result.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!));
        }
        if (year < 2000)
        {
            return Result<IReadOnlyList<SfsDeclaration>>.Failure(ErrorCodes.ValidationFailed, "year must be >= 2000.");
        }

        var body = JsonSerializer.Serialize(
            new { idnp = idnpResult.Value.Value, year, asOfUtc = _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            ExternalClientSerialization.Default);

        var raw = await _mconnect.CallAsync("SFS.GetSalaryDeclarations", body, ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<IReadOnlyList<SfsDeclaration>>.From(Result.Failure(raw.ErrorCode!, raw.ErrorMessage!));
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            JsonElement listElement = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("declarations", out var inner) => inner,
                _ => default,
            };

            if (listElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("SFS.GetSalaryDeclarations returned an unrecognised payload shape.");
                return Result<IReadOnlyList<SfsDeclaration>>.Failure(ErrorCodes.MConnectFailed, "SFS returned an unrecognised payload shape.");
            }

            var declarations = listElement.Deserialize<List<SfsDeclaration>>(ExternalClientSerialization.Default) ?? new List<SfsDeclaration>();
            return Result<IReadOnlyList<SfsDeclaration>>.Success(declarations);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SFS.GetSalaryDeclarations returned a malformed JSON payload.");
            return Result<IReadOnlyList<SfsDeclaration>>.Failure(ErrorCodes.MConnectFailed, "SFS returned a malformed JSON payload.");
        }
    }
}
