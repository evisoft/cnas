using System.Text.Json;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Prefill;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// R0363 — placeholder implementation of <see cref="IRsudGateway"/>. Returns one
/// hand-coded activity-period delta so the refresh pipeline can be tested end-to-end
/// against <c>ContributorActivityPeriod</c>.
/// </summary>
/// <param name="logger">Structured logger.</param>
public sealed class MockRsudGateway(ILogger<MockRsudGateway> logger) : IRsudGateway, IPrefillSourceAdapter
{
    private readonly ILogger<MockRsudGateway> _logger = logger;

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
    {
        _logger.LogInformation("MockRsudGateway returning hand-coded activity-period delta.");
        _ = idnp;
        _ = ct;

        var activityPayload = JsonSerializer.Serialize(new
        {
            employerCode = "1003600999999",
            position = "Specialist",
            monthlySalary = 12000m,
        });
        IReadOnlyList<ProfileRefreshDeltaDto> deltas =
        [
            new ProfileRefreshDeltaDto(
                ChildEntityType: "Activity",
                FieldName: "employerCode",
                OldValue: null,
                NewValue: "1003600999999",
                PayloadJson: activityPayload),
        ];
        return Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(deltas));
    }

    /// <inheritdoc />
    public string SourceCode => PrefillSources.Rsud;

    /// <summary>
    /// R0552 / R0562 — RSUD's pre-fill stub. The real RSUD register carries an
    /// address shadow derived from the citizen's most recent registered address
    /// change; until the NDA-gated wiring lands this stub returns an empty
    /// dictionary so the merge logic exercises the "RSP-only" path by default.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
    {
        _logger.LogInformation("MockRsudGateway returning empty pre-fill values (placeholder).");
        _ = idnp;
        _ = ct;
        IReadOnlyDictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        return Task.FromResult(values);
    }
}
