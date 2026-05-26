using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — service façade over the mass-recalculation engine.
/// Hosts the dry-run / apply triggers, the per-run lookups, and the operator
/// cherry-pick (reject / apply-approved) surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> Both <see cref="StartDryRunAsync"/> and
/// <see cref="StartApplyAsync"/> consult <c>IPeakHourGate</c> at the top of
/// the call. A SKIP decision returns <see cref="ErrorCodes.Conflict"/> with
/// the stable code <see cref="ErrorCodes.PeakHourBlocked"/> so operators
/// can retry off-peak or escalate via the gate's override.
/// </para>
/// <para>
/// <b>Audit attribution.</b> Each interesting transition is captured by a
/// stable audit code at Critical severity:
/// <list type="bullet">
///   <item><see cref="StartDryRunAsync"/> → <c>MASS_RECALC.DRY_RUN_STARTED</c>.</item>
///   <item><see cref="StartApplyAsync"/> → <c>MASS_RECALC.APPLY_STARTED</c>.</item>
///   <item><see cref="RejectResultAsync"/> → <c>MASS_RECALC.RESULT_REJECTED</c>.</item>
///   <item><see cref="ApplyApprovedResultsAsync"/> → <c>MASS_RECALC.APPROVED_APPLIED</c>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IMassRecalculationService
{
    /// <summary>
    /// Triggers a DryRun against the supplied legal-change event. The
    /// engine enumerates every active decision in scope, invokes the
    /// registered strategy per benefit kind, and persists per-decision
    /// result rows WITHOUT mutating the underlying decision rows.
    /// </summary>
    /// <param name="legalChangeSqid">Sqid-encoded legal-change-event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run DTO on success; peak-hour / not-found / conflict otherwise.</returns>
    Task<Result<RecalculationRunDto>> StartDryRunAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers an Apply run against the supplied legal-change event. The
    /// engine enumerates affected decisions, recomputes per strategy, AND
    /// writes the new amounts back via <c>strategy.ApplyAsync</c>.
    /// </summary>
    /// <param name="legalChangeSqid">Sqid-encoded legal-change-event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run DTO on success; peak-hour / not-found / conflict otherwise.</returns>
    Task<Result<RecalculationRunDto>> StartApplyAsync(
        string legalChangeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a single run by its Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run DTO on success; invalid-sqid / not-found otherwise.</returns>
    Task<Result<RecalculationRunDto>> GetRunByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a run plus its filtered, paged decision-results list.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run-with-results envelope on success.</returns>
    Task<Result<RecalculationRunDetailsDto>> GetRunDetailsAsync(
        string sqid,
        RecalculationResultFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Lists runs matching the supplied filter.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Page DTO on success.</returns>
    Task<Result<RecalculationRunPageDto>> ListRunsAsync(
        RecalculationRunFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one Computed result row as Rejected with a rationale. Subsequent
    /// <see cref="ApplyApprovedResultsAsync"/> invocations skip this row.
    /// </summary>
    /// <param name="resultSqid">Sqid-encoded result id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success; conflict when status is not Computed.</returns>
    Task<Result<RecalculationDecisionResultDto>> RejectResultAsync(
        string resultSqid,
        RecalculationResultRejectInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Iterates every Computed-status result on the run and invokes
    /// <c>strategy.ApplyAsync</c> per row. Rows whose strategy is missing
    /// transition to Skipped with <see cref="ErrorCodes.NoStrategyRegistered"/>;
    /// rows that throw transition to Failed; successful rows transition to
    /// Applied with <c>AppliedAt</c> stamped.
    /// </summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated run DTO on success.</returns>
    Task<Result<RecalculationRunDto>> ApplyApprovedResultsAsync(
        string runSqid,
        CancellationToken cancellationToken = default);
}
