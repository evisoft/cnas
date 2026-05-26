using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ContributorProfileUpdates;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — external-data refresh of contributor profiles. Pulls
/// the most recent profile fields from upstream registries (RSP / RSUD / SI SFS) via
/// MConnect (or a partner-direct fallback per R0104) and applies them to our local
/// cache by invoking the same <c>IContributorLinkedEntitiesService</c> writers that
/// power the admin-edit and approval flows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auditing.</b> Each completed run emits a Sensitive <c>PROFILE.REFRESH.COMPLETED</c>
/// audit row carrying <c>{ source, contributorSqid, rowsApplied, outcome }</c>.
/// </para>
/// <para>
/// <b>Scheduled batch job (deferred).</b> A future
/// <c>ProfileRefreshScheduledJob</c> calls this service in a per-contributor loop;
/// activation is gated by
/// <c>ProfileRefreshOptions.EnableScheduledRefresh</c> (default off) until the per-
/// system MConnect contracts land.
/// </para>
/// </remarks>
public interface IProfileRefreshService
{
    /// <summary>
    /// Refreshes the contributor's profile from <paramref name="source"/>. Calls the
    /// matching gateway, applies each returned delta via the contributor-side writer,
    /// and persists one <c>ProfileRefreshRun</c> row capturing the outcome.
    /// </summary>
    /// <param name="source">Upstream source code: <c>RSP</c> / <c>RSUD</c> / <c>SI_SFS</c>.</param>
    /// <param name="contributorId">Internal <c>InsuredPerson</c> id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ProfileRefreshRunDto>> RefreshFromSourceAsync(string source, long contributorId, CancellationToken ct = default);

    /// <summary>
    /// Lists the most recent refresh runs, newest first. Used by the operator dashboard
    /// to monitor recent activity and diagnose failures.
    /// </summary>
    /// <param name="take">Maximum rows to return (clamped to <c>[1, 500]</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<ProfileRefreshRunDto>>> ListRecentAsync(int take, CancellationToken ct = default);
}
