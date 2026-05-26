using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0933 / TOR §10.1 — terminate-prior-on-acceptance lifecycle. Compares the
/// newly accepted decision with the most recent prior active decision for the
/// same <c>(Solicitant, ServiceCode)</c> pair, terminates the prior, and
/// records an append-only <c>DecisionSupersession</c> row pointing back at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The same Solicitant may hold concurrent active decisions for
/// distinct services (e.g. an old-age pension + a disability allowance). The
/// terminator narrows by <c>ServicePassport.Code</c> so cross-service replacement
/// is impossible — only same-service replacement triggers supersession.
/// </para>
/// <para>
/// <b>Idempotency.</b> Calling <see cref="TerminateOnAcceptanceAsync"/> twice
/// for the same new decision returns the existing supersession row (no double
/// counting, no second audit). The natural-key uniqueness lives on the
/// <c>DecisionSupersession</c> EF configuration.
/// </para>
/// <para>
/// <b>Lower-sum warning.</b> The compare endpoint surfaces a structured
/// warning when the new decision's sum is lower than the prior's; the decider
/// UI must then re-confirm before invoking the terminator. The terminator
/// itself does NOT enforce the warning — it only records the link — so the
/// guard lives at the call site (controller / approval workspace).
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped. Depends on the per-request
/// <see cref="Cnas.Ps.Application.Abstractions.ICnasDbContext"/> +
/// <see cref="ISqidService"/> + <see cref="ICnasTimeProvider"/> +
/// <see cref="IAuditService"/>.
/// </para>
/// </remarks>
public interface IPriorDecisionTerminator
{
    /// <summary>
    /// Finds the prior active decision for the same (Solicitant, ServiceCode)
    /// pair as <paramref name="newDecisionId"/>, terminates it
    /// (<c>ApplicationStatus.Closed</c>), and records a
    /// <c>DecisionSupersession</c> row pointing at it.
    /// </summary>
    /// <param name="newDecisionId">
    /// Raw <c>ServiceApplication.Id</c> of the newly accepted decision.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <see cref="Result{T}.Success"/> with the persisted (or pre-existing,
    ///       idempotent) supersession DTO when a prior decision was located AND
    ///       terminated.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="Result{T}.Success"/> with <c>null</c> when no prior active
    ///       decision exists — this is the common path for first-time applicants.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="Result{T}.Failure"/> with <see cref="ErrorCodes.NotFound"/>
    ///       when the new decision id does not resolve to an active
    ///       <c>ServiceApplication</c> row.
    ///     </description>
    ///   </item>
    /// </list>
    /// </returns>
    Task<Result<DecisionSupersessionDto?>> TerminateOnAcceptanceAsync(
        long newDecisionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the (PriorAmount, NewAmount, Difference) tuple between
    /// <paramref name="newDecisionId"/> and the most recent prior active
    /// decision for the same (Solicitant, ServiceCode) pair without mutating
    /// any state.
    /// </summary>
    /// <param name="newDecisionId">
    /// Raw <c>ServiceApplication.Id</c> of the new decision.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> carrying the comparison DTO (with
    /// <c>HasPrior=false</c> when no prior exists);
    /// <see cref="Result{T}.Failure"/> with <see cref="ErrorCodes.NotFound"/>
    /// when the new decision id is unknown.
    /// </returns>
    Task<Result<DecisionComparisonDto>> CompareAsync(
        long newDecisionId,
        CancellationToken cancellationToken = default);
}
