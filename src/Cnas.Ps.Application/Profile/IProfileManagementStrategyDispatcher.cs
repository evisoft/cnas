using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — selects and invokes the right
/// <see cref="IProfileManagementStrategy"/> for an incoming profile
/// mutation. Owns ZERO domain logic; the dispatcher only routes by
/// <see cref="IProfileManagementStrategy.StrategyKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton lifetime.</b> The dispatcher captures the
/// <see cref="IServiceProvider"/> at construction time and resolves the
/// scoped strategy instances on every dispatch — that way the singleton
/// never holds a captive scoped collaborator (per CLAUDE.md "DI lifetime"
/// hygiene).
/// </para>
/// <para>
/// <b>Unknown key.</b> When no registered strategy matches the supplied
/// key the dispatcher returns
/// <see cref="ErrorCodes.NotFound"/> with a message identifying the key that
/// failed to resolve. The matching is case-insensitive but the canonical
/// key form is mixed-case (<c>Ui</c> / <c>Form</c> / <c>ExternalSync</c>).
/// </para>
/// </remarks>
public interface IProfileManagementStrategyDispatcher
{
    /// <summary>
    /// Resolves the strategy whose
    /// <see cref="IProfileManagementStrategy.StrategyKey"/> matches
    /// <paramref name="strategyKey"/> (case-insensitively) and invokes
    /// <see cref="IProfileManagementStrategy.ApplyAsync"/>. Returns
    /// <see cref="ErrorCodes.NotFound"/> when no strategy matches.
    /// </summary>
    /// <param name="strategyKey">Stable strategy discriminator — one of
    /// <see cref="ProfileManagementStrategyKeys"/>.</param>
    /// <param name="profileSqid">Sqid-encoded user-profile id. May be empty —
    /// see <see cref="IProfileManagementStrategy.ApplyAsync"/> for the
    /// per-channel resolution semantics.</param>
    /// <param name="input">Channel-specific input.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// The strategy's <see cref="Result{T}"/>, or
    /// <see cref="ErrorCodes.NotFound"/> when no strategy is registered
    /// for the supplied key.
    /// </returns>
    Task<Result<ProfileOutput>> DispatchAsync(
        string strategyKey,
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken);
}
