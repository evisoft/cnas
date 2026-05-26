using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0942 / TOR §10.1 — automatic fallback from a refused pension decision to a
/// social-allowance follow-up application. Implements the
/// "refused pension → alocație socială" cascade referenced by the §3.3-H rule.
/// </summary>
/// <remarks>
/// <para>
/// Invoked by <c>IDecisionWorkflowService</c> when a decision lands in
/// <see cref="Cnas.Ps.Core.Domain.ApplicationStatus.Rejected"/>. The
/// implementation checks whether the parent passport belongs to the
/// <c>Pensie*</c> family (heuristic on the passport's stable Code containing
/// the literal <c>"PENSION"</c>); when so, it opens a Draft
/// <c>ServiceApplication</c> targeting the configured
/// <c>WorkflowOptions.SocialAllowancePassportCode</c> against the same
/// Solicitant, prefilled from the refused application's payload.
/// </para>
/// <para>
/// <b>Idempotency.</b> Re-running the cascade for the same refused decision
/// returns the existing follow-up draft without creating duplicates. Detection
/// keys on the refused-decision id stored in the new draft's
/// <c>FormPayloadJson</c> (<c>cascadeFromDecisionId</c>).
/// </para>
/// <para>
/// <b>Audit.</b> A Notice <c>DECISION.FALLBACK_INITIATED</c> row is emitted on
/// every successful cascade. Non-trigger branches do NOT audit (they are
/// uninteresting from a forensics standpoint).
/// </para>
/// </remarks>
public interface IRefusedPensionFallbackCascade
{
    /// <summary>
    /// Evaluates the cascade against the supplied refused decision. Returns a
    /// structured outcome describing what happened.
    /// </summary>
    /// <param name="refusedDecisionId">
    /// Raw <c>ServiceApplication.Id</c> of the decision that just landed in
    /// <see cref="Cnas.Ps.Core.Domain.ApplicationStatus.Rejected"/>.
    /// </param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the documented outcome shape on
    /// every branch (including no-op); <see cref="Result{T}.Failure"/> with
    /// <see cref="ErrorCodes.NotFound"/> only when the decision id is
    /// completely unknown.
    /// </returns>
    Task<Result<FallbackCascadeOutcomeDto>> EvaluateAsync(
        long refusedDecisionId,
        CancellationToken ct = default);
}
