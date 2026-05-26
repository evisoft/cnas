using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — UI self-service strategy. Delegates to the
/// existing <see cref="IProfileService.UpdateMyContactAsync(ProfileContactInput, CancellationToken)"/>
/// path so the citizen Blazor portal and the dispatcher share one
/// validation + persistence pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>profileSqid is ignored.</b> The caller-resolution for the UI channel
/// runs server-side via <c>ICallerContext.UserId</c> on the wrapped
/// <see cref="IProfileService"/>. We deliberately do NOT trust the
/// route-supplied <c>profileSqid</c> here — that would let a logged-in
/// citizen impersonate another profile by varying the route segment.
/// </para>
/// <para>
/// <b>Round-trip semantics.</b> After the contact update lands we call
/// <see cref="IProfileService.GetMineAsync(CancellationToken)"/> so the
/// strategy returns the post-mutation <see cref="ProfileOutput"/> in line
/// with the <see cref="IProfileManagementStrategy"/> contract.
/// </para>
/// </remarks>
/// <param name="profiles">Underlying profile service.</param>
public sealed class UiProfileManagementStrategy(IProfileService profiles)
    : IProfileManagementStrategy
{
    private readonly IProfileService _profiles = profiles;

    /// <inheritdoc />
    public string StrategyKey => ProfileManagementStrategyKeys.Ui;

    /// <inheritdoc />
    public async Task<Result<ProfileOutput>> ApplyAsync(
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The UI channel requires DisplayName per ProfileContactInput's
        // service-layer contract. Validate at the boundary so the dispatcher
        // surfaces a stable error code without round-tripping the database.
        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "Display name is required for the UI management strategy.");
        }

        var contactInput = new ProfileContactInput(
            DisplayName: input.DisplayName,
            Email: input.Email,
            Phone: input.Phone);
        var update = await _profiles
            .UpdateMyContactAsync(contactInput, cancellationToken)
            .ConfigureAwait(false);
        if (update.IsFailure)
        {
            return Result<ProfileOutput>.Failure(update.ErrorCode!, update.ErrorMessage!);
        }

        // Re-read so the dispatcher returns the post-mutation projection.
        return await _profiles.GetMineAsync(cancellationToken).ConfigureAwait(false);
    }
}
