using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1505 / TOR §3.7-F — recovery-workflow service backing the CNAS-initiated
/// "Decizia de recuperare a sumelor" lifecycle. Mints a <c>Document</c> row via
/// the <c>DecizieRecuperareSumeTemplate</c> on initiation, then transitions the
/// row through acknowledge / partial-recover / full-recover states as the
/// solicitant repays.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistence shape.</b> The lifecycle is modelled on the existing
/// <c>Document</c> aggregate (Kind=Decision) rather than a separate aggregate
/// — the registry projection (R1601) already walks Document rows of decision
/// kind, so reusing the same table keeps the source of truth singular. The
/// integer <c>Document.Verdict</c> column carries the current
/// <c>RecoveryDecisionStatus</c> ordinal; <c>Document.VerdictNote</c> carries a
/// JSON envelope with the amount + reason + recovered-so-far running total.
/// </para>
/// <para>
/// <b>Audit.</b> <c>DECISION.RECOVERY_INITIATED</c> emits Critical on
/// successful initiation; <c>DECISION.RECOVERY_ACKNOWLEDGED</c> +
/// <c>DECISION.RECOVERY_RECOVERED</c> emit Warning on lifecycle transitions.
/// </para>
/// <para>
/// <b>Idempotency.</b> A double-acknowledge against an already-acknowledged
/// decision is a no-op success (no extra audit row, no double notification);
/// a recovered-call with non-positive amount fails
/// <see cref="ErrorCodes.ValidationFailed"/>.
/// </para>
/// </remarks>
public interface IRecoveryDecisionService
{
    /// <summary>
    /// Issues a new recovery decision against the supplied beneficiary.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded id of the targeted beneficiary.</param>
    /// <param name="amount">Recovery amount in MDL (must be strictly positive).</param>
    /// <param name="reason">Free-text justification (3-500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the freshly-minted decision DTO on
    /// success; <see cref="Result{T}.Failure"/> with
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed solicitant id,
    /// <see cref="ErrorCodes.NotFound"/> when the solicitant is unknown, or
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad input.
    /// </returns>
    Task<Result<RecoveryDecisionDto>> InitiateAsync(
        string solicitantSqid,
        decimal amount,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a recovery decision as acknowledged by the solicitant. Idempotent
    /// — a second call on an already-acknowledged decision returns
    /// <see cref="Result.Success"/> without an extra audit / notification.
    /// </summary>
    /// <param name="decisionSqid">Sqid-encoded id of the recovery decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success or a stable failure code
    /// (<see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.InvalidSqid"/>
    /// / <see cref="ErrorCodes.Conflict"/> when the decision is already in a
    /// terminal recovered state).
    /// </returns>
    Task<Result> MarkAcknowledgedAsync(
        string decisionSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a recovery payment against the decision. Partial recoveries
    /// transition the row to <see cref="RecoveryDecisionStatus.PartiallyRecovered"/>;
    /// a recovery that brings the running total to (or past) the original amount
    /// transitions it to <see cref="RecoveryDecisionStatus.FullyRecovered"/>.
    /// </summary>
    /// <param name="decisionSqid">Sqid-encoded id of the recovery decision.</param>
    /// <param name="recoveredAmount">Amount recovered this round in MDL (strictly positive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success or a stable failure code.
    /// </returns>
    Task<Result> MarkRecoveredAsync(
        string decisionSqid,
        decimal recoveredAmount,
        CancellationToken cancellationToken = default);
}
