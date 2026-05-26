using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — pluggable strategy used by the
/// <see cref="IProfileManagementStrategyDispatcher"/> to apply a profile
/// mutation. The TOR enumerates three management strategies for a citizen
/// profile: UI self-service, form intake, and external sync via MConnect.
/// Each maps 1:1 to a registered <see cref="IProfileManagementStrategy"/>
/// implementation; the dispatcher selects by <see cref="StrategyKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strategy keys.</b> Implementations MUST return one of the stable
/// constants declared on <see cref="ProfileManagementStrategyKeys"/>
/// (<c>Ui</c> / <c>Form</c> / <c>ExternalSync</c>). The dispatcher matches
/// case-insensitively but the canonical form is mixed-case so dashboards stay
/// readable.
/// </para>
/// <para>
/// <b>Registration.</b> Strategies are registered as scoped collaborators
/// (each depends on the per-request <c>ICnasDbContext</c> via the underlying
/// <see cref="UseCases.IProfileService"/> implementation). The dispatcher
/// itself is registered Singleton — it holds no per-request state.
/// </para>
/// <para>
/// <b>Result semantics.</b> Every strategy MUST surface a populated
/// <see cref="ProfileOutput"/> on success (so the dispatcher can return the
/// post-mutation view to the caller without a follow-up GET). On failure the
/// strategy MUST return a stable <see cref="ErrorCodes"/> code so the API
/// boundary can translate to the right HTTP status without parsing the
/// human-readable message.
/// </para>
/// </remarks>
public interface IProfileManagementStrategy
{
    /// <summary>
    /// Stable mixed-case strategy discriminator. MUST be one of
    /// <see cref="ProfileManagementStrategyKeys.Ui"/>,
    /// <see cref="ProfileManagementStrategyKeys.Form"/>, or
    /// <see cref="ProfileManagementStrategyKeys.ExternalSync"/>.
    /// </summary>
    string StrategyKey { get; }

    /// <summary>
    /// Applies the supplied management input to the addressed profile. The
    /// implementation owns the validation + persistence semantics for its
    /// channel; the dispatcher itself only routes by
    /// <see cref="StrategyKey"/>.
    /// </summary>
    /// <param name="profileSqid">
    /// Sqid-encoded user-profile id (CLAUDE.md RULE 3). May be empty for
    /// strategies that resolve the target user from the inbound channel
    /// (UI strategy uses the caller context, external sync uses payload
    /// identifiers); the implementation is responsible for resolving the
    /// right user.
    /// </param>
    /// <param name="input">
    /// Channel-specific input. See
    /// <see cref="ProfileManagementInput"/> for the carrier shape and the
    /// per-channel field semantics.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the post-mutation
    /// <see cref="ProfileOutput"/>; failure with a stable
    /// <see cref="ErrorCodes"/> code on any rejection.
    /// </returns>
    Task<Result<ProfileOutput>> ApplyAsync(
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// R0622 — stable string constants for the three management-strategy
/// discriminators. Centralised in one place so the dispatcher, the strategies,
/// and the controllers cannot drift.
/// </summary>
public static class ProfileManagementStrategyKeys
{
    /// <summary>UI self-service strategy — citizen edits via the Blazor portal.</summary>
    public const string Ui = "Ui";

    /// <summary>Form-intake strategy — paper / front-desk form translated to a profile update.</summary>
    public const string Form = "Form";

    /// <summary>External-sync strategy — MConnect / RSP authoritative source push.</summary>
    public const string ExternalSync = "ExternalSync";
}
