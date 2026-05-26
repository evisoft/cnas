using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1502 / TOR §3.7-C — recompute pipeline for an existing benefit decision.
/// Detects the delta between the prior decision's amount and the recompute
/// result, persists a new <c>Document</c> of the appropriate template
/// (<c>DecizieAjustareSumeTemplate</c> for a top-up, or
/// <c>DecizieRecuperareSumeTemplate</c> for a debit), and dispatches a
/// citizen notification via <c>INotificationTriggerDispatcher</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recompute target.</b> The service uses the prior decision's
/// <c>FormPayloadJson</c> as the source of facts, parses out the
/// <c>monthlyAmountMdl</c> / <c>amount</c> key (best-effort; same heuristic as
/// <c>PriorDecisionTerminator</c>) and treats the value supplied on the
/// recompute input as the new amount. A subsequent iteration will wire the full
/// <c>JsonRulesDecisionEngine</c> recompute via the linked
/// <c>ServicePassport.DecisionRulesJson</c>; this iteration locks the
/// scaffolding + audit + document-emission paths.
/// </para>
/// <para>
/// <b>Audit.</b> A Critical <c>DECISION.RECOMPUTED</c> row is emitted on every
/// invocation that actually changes state. Zero-delta no-ops do NOT audit.
/// </para>
/// </remarks>
public interface IDecisionRecomputeService
{
    /// <summary>
    /// Recomputes the supplied prior decision against the new amount and persists
    /// the resulting adjustment / recuperare document.
    /// </summary>
    /// <param name="priorDecisionSqid">Sqid-encoded id of the prior decision.</param>
    /// <param name="reason">Stable reason code driving the recompute.</param>
    /// <param name="newMonthlyAmountMdl">
    /// Recomputed monthly amount in MDL. Compared against the prior decision's
    /// stored amount to derive the delta.
    /// </param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the outcome on success;
    /// <see cref="Result{T}.Failure"/> with
    /// <see cref="ErrorCodes.NotFound"/> when the prior decision is unknown,
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed input,
    /// <see cref="ErrorCodes.ValidationFailed"/> on a negative new amount.
    /// </returns>
    Task<Result<DecisionRecomputeOutcomeDto>> RecomputeAsync(
        string priorDecisionSqid,
        DecisionRecomputeReason reason,
        decimal newMonthlyAmountMdl,
        CancellationToken cancellationToken = default);
}
