using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — external-sync strategy. The default implementation
/// is a stub that returns
/// <see cref="ErrorCodes.ProfileExternalSyncNotConfigured"/> while the
/// MConnect transport remains externally gated
/// (EGOV-INTEGRATION-GAP §MConnect / per R0630 fallback path).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the strategy ships now.</b> Even with the transport gated, the
/// dispatcher + strategy seam is the right shape for downstream
/// integrators to target — by exposing the contract today we keep the wire
/// shape (<see cref="ExternalProfileSyncInput"/>) stable so the eventual
/// MConnect / RSP adapter can drop in without a re-issue of the
/// dispatcher's public surface.
/// </para>
/// <para>
/// <b>Validation before refusal.</b> The strategy still validates that the
/// envelope is well-formed (non-null + non-empty <c>SourceSystem</c> +
/// non-null <c>Payload</c>) so a malformed call surfaces a precise
/// <see cref="ErrorCodes.ValidationFailed"/> instead of getting swallowed
/// by the gated-refusal path.
/// </para>
/// </remarks>
public sealed class ExternalSyncProfileManagementStrategy : IProfileManagementStrategy
{
    /// <inheritdoc />
    public string StrategyKey => ProfileManagementStrategyKeys.ExternalSync;

    /// <inheritdoc />
    public Task<Result<ProfileOutput>> ApplyAsync(
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ExternalSync is null)
        {
            return Task.FromResult(Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "ExternalSync envelope is required for the ExternalSync management strategy."));
        }
        if (string.IsNullOrWhiteSpace(input.ExternalSync.SourceSystem))
        {
            return Task.FromResult(Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "ExternalSync.SourceSystem is required."));
        }
        if (input.ExternalSync.Payload is null)
        {
            return Task.FromResult(Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "ExternalSync.Payload is required."));
        }

        // Externally gated — return the stable refusal code so dashboards can
        // chart "external sync attempted while gated" cleanly.
        return Task.FromResult(Result<ProfileOutput>.Failure(
            ErrorCodes.ProfileExternalSyncNotConfigured,
            $"External sync adapter for source system '{input.ExternalSync.SourceSystem}' " +
            "is not configured in this build (EGOV-INTEGRATION-GAP §MConnect)."));
    }
}
