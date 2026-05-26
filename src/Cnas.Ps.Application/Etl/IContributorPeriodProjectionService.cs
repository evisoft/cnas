using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — orchestrator over the contributor period-projection
/// pipeline. Reads the supersession child tables for an
/// <c>InsuredPerson</c> (Persoană asigurată) and rebuilds the
/// <c>ContributorPeriodProjections</c> snapshot table so reports can resolve
/// "as-of date X" with a single bounded-period query instead of scanning
/// every source supersession chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rebuild side.</b> <see cref="RebuildForContributorAsync"/> loads every
/// <c>ContributorAddress</c>, <c>ContributorContact</c>,
/// <c>ContributorActivityPeriod</c> and <c>ContributorCivilStatus</c> row for
/// the contributor (including historical / ended ones), flattens them via
/// <see cref="PeriodSliceBuilder"/>, and DELETE-then-INSERTs the resulting
/// slices into the projection table. Idempotent.
/// </para>
/// <para>
/// <b>Batch side.</b> <see cref="RebuildAllAsync"/> iterates every contributor
/// (including historical / soft-deleted ones — see service implementation for
/// the soft-delete handling) and emits exactly one
/// <c>ETL.PERIOD_PROJECTION.COMPLETED</c> audit row with the run totals.
/// Idempotent — partial-failure recovery is "re-run the whole batch".
/// </para>
/// <para>
/// <b>Read side.</b> <see cref="QueryAsync"/> is the period-aware lookup
/// surface — it returns every projection row whose half-open
/// <c>[PeriodStartUtc, PeriodEndUtc)</c> interval covers the supplied
/// <c>asOfUtc</c>. Most lookups will hit exactly one row; the API returns a
/// list so boundary edge cases (two adjacent slices touching the same
/// instant) are visible to callers.
/// </para>
/// </remarks>
public interface IContributorPeriodProjectionService
{
    /// <summary>
    /// Rebuilds the projection rows for a single contributor. Idempotent —
    /// existing rows for the contributor are deleted before insertion, so a
    /// re-run produces the same slice set as long as the underlying source
    /// rows are unchanged.
    /// </summary>
    /// <param name="contributorId">
    /// Internal raw <c>long</c> id of the <c>InsuredPerson</c>. Sqid
    /// encoding/decoding happens at the API boundary; the service works in
    /// raw ids.
    /// </param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// A success <see cref="Result{T}"/> carrying the per-contributor run
    /// summary. Failures are reserved for truly exceptional conditions —
    /// "no source rows" is success with <c>SlicesCreated = 0</c>.
    /// </returns>
    Task<Result<ContributorPeriodProjectionRunDto>> RebuildForContributorAsync(
        long contributorId,
        CancellationToken ct);

    /// <summary>
    /// Rebuilds the projection rows for every active contributor in the
    /// system. Loops over <see cref="RebuildForContributorAsync"/> and emits
    /// a single <c>ETL.PERIOD_PROJECTION.COMPLETED</c> audit row carrying the
    /// totals (contributors processed, slices created, duration).
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// A success <see cref="Result{T}"/> carrying the run summary.
    /// </returns>
    Task<Result<ContributorPeriodProjectionRunDto>> RebuildAllAsync(CancellationToken ct);

    /// <summary>
    /// Returns every projection row for <paramref name="contributorId"/>
    /// whose half-open <c>[PeriodStartUtc, PeriodEndUtc)</c> interval covers
    /// <paramref name="asOfUtc"/>. Most lookups will hit exactly one row;
    /// the API returns a list so callers can surface boundary edge cases.
    /// </summary>
    /// <param name="contributorId">
    /// Internal raw <c>long</c> id of the <c>InsuredPerson</c>.
    /// </param>
    /// <param name="asOfUtc">UTC instant being asked about.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// Read-only list of matching projection DTOs. Empty when no projection
    /// covers the instant (or no projection exists for the contributor).
    /// </returns>
    Task<IReadOnlyList<ContributorPeriodProjectionDto>> QueryAsync(
        long contributorId,
        DateTime asOfUtc,
        CancellationToken ct);
}
