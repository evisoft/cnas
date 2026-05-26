using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — pipeline-pluggable strategy dispatched by the
/// mass-recalculation orchestrator per <see cref="BenefitType"/>. The engine
/// is intentionally generic: each benefit kind ships a concrete strategy
/// that knows how to enumerate affected decisions, project the new amount,
/// and write the new amount back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration.</b> Strategies are registered in DI as
/// <c>IEnumerable&lt;IBenefitRecalculationStrategy&gt;</c>; the orchestrator
/// dispatches by <see cref="BenefitType"/>. When NO strategy is registered
/// for a kind in scope, every affected decision is tagged
/// <see cref="RecalculationResultStatus.Skipped"/> with reason
/// <see cref="ErrorCodes.NoStrategyRegistered"/>.
/// </para>
/// <para>
/// <b>Multiple strategies.</b> Registering more than one strategy for the
/// same <see cref="BenefitType"/> is forbidden by the orchestrator — the
/// duplicate is reported as a startup error so operators catch the
/// misconfiguration early.
/// </para>
/// </remarks>
public interface IBenefitRecalculationStrategy
{
    /// <summary>Stable enum-name string identifying the <see cref="BenefitType"/> this strategy handles.</summary>
    string BenefitType { get; }

    /// <summary>
    /// Returns the internal ids of every active benefit decision affected
    /// by the supplied legal-change event. The implementation MUST scope to
    /// the event's <c>EffectiveFrom</c> month so an event for 2026-07 does
    /// not perturb decisions that expired in 2026-06.
    /// </summary>
    /// <param name="evt">The legal-change event driving the run.</param>
    /// <param name="db">Read-replica context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Decision ids in scope (may be empty).</returns>
    Task<IReadOnlyList<long>> EnumerateInScopeDecisionIdsAsync(
        LegalChangeEvent evt,
        IReadOnlyCnasDbContext db,
        CancellationToken cancellationToken);

    /// <summary>
    /// Projects the new amount for a single decision under the supplied
    /// legal-change event. MUST NOT mutate any rows.
    /// </summary>
    /// <param name="decisionId">Internal id of the decision to recompute.</param>
    /// <param name="evt">The legal-change event.</param>
    /// <param name="db">Read-replica context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recalc outcome (Computed / Skipped / Failed).</returns>
    Task<BenefitRecalculationOutcome> RecomputeAsync(
        long decisionId,
        LegalChangeEvent evt,
        IReadOnlyCnasDbContext db,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the new amount back to the underlying decision aggregate.
    /// Called only from the Apply phase. The orchestrator manages the
    /// transaction — the strategy MUST NOT call <c>SaveChangesAsync</c>.
    /// </summary>
    /// <param name="result">The Computed result row being applied.</param>
    /// <param name="db">Writer context (transaction managed by caller).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success / failure.</returns>
    Task<Result> ApplyAsync(
        RecalculationDecisionResult result,
        ICnasDbContext db,
        CancellationToken cancellationToken);
}
