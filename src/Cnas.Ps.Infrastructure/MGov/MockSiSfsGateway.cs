using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Prefill;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// R0363 — placeholder implementation of <see cref="ISiSfsGateway"/>. Returns no deltas
/// by default so the "NoChange" branch of <c>IProfileRefreshService.RefreshFromSourceAsync</c>
/// has a stable production-time exerciser.
/// </summary>
/// <remarks>
/// Tests that need to assert PartialFailure / Success paths inject their own
/// <see cref="ISiSfsGateway"/> double. This mock is the safe production default — it
/// never causes downstream mutations until the NDA-gated SI SFS contracts land.
/// </remarks>
/// <param name="logger">Structured logger.</param>
public sealed class MockSiSfsGateway(ILogger<MockSiSfsGateway> logger) : ISiSfsGateway, IPrefillSourceAdapter
{
    private readonly ILogger<MockSiSfsGateway> _logger = logger;

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
    {
        _logger.LogInformation("MockSiSfsGateway returning empty delta set (placeholder).");
        _ = idnp;
        _ = ct;
        IReadOnlyList<ProfileRefreshDeltaDto> deltas = [];
        return Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(deltas));
    }

    /// <inheritdoc />
    public string SourceCode => PrefillSources.SiSfs;

    /// <summary>
    /// R0552 / R0562 — SI SFS pre-fill stub. Empty by default until the NDA-gated
    /// SOAP integration lands; tests inject their own <see cref="IPrefillSourceAdapter"/>
    /// double when they need to exercise a populated SI SFS path.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
    {
        _logger.LogInformation("MockSiSfsGateway returning empty pre-fill values (placeholder).");
        _ = idnp;
        _ = ct;
        IReadOnlyDictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        return Task.FromResult(values);
    }
}
