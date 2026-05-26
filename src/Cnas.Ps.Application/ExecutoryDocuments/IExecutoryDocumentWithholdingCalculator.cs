using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ExecutoryDocuments;

/// <summary>
/// R1406 / TOR §3.6-G — pure-calculation service that translates the set of
/// <see cref="Cnas.Ps.Core.Domain.ExecutoryDocumentStatus.Active"/> documents
/// targeting a debtor into a per-payment withholding plan.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure computation.</b> The calculator does NOT mutate the registry; it
/// pulls the relevant Active rows, computes per-document withholding using
/// each row's <c>WithholdingMode</c>, applies the 70% cap (art. 156 CMP),
/// and returns the plan. The caller (payment-dispatcher) decides whether to
/// commit the plan via <see cref="IExecutoryDocumentService.RecordWithholdingAsync"/>.
/// </para>
/// <para>
/// <b>Cap rule.</b> The total of all <c>AllocatedMdl</c> rows must not exceed
/// 70% of the gross benefit. The cap is applied in PriorityRank ASC order:
/// the highest-priority document gets its full requested amount, then the
/// next, etc., until the cap is reached. Subsequent rows get whatever residual
/// capacity remains (with rationale <c>PARTIAL_ALLOCATION</c>) or zero (with
/// rationale <c>CAP_EXCEEDED</c>).
/// </para>
/// </remarks>
public interface IExecutoryDocumentWithholdingCalculator
{
    /// <summary>
    /// Computes the per-payment withholding plan for the supplied debtor +
    /// benefit. Pulls Active documents whose effective window covers
    /// <paramref name="benefitPeriod"/>, orders by PriorityRank ASC, applies
    /// per-row computation and the 70% cap.
    /// </summary>
    /// <param name="debtorIdnp">Plaintext IDNP of the debtor; the calculator hashes internally for the lookup.</param>
    /// <param name="grossBenefitMdl">Gross benefit (MDL) before any withholding.</param>
    /// <param name="legalMinimumMdl">Legal minimum-subsistence floor (MDL) used by the <c>FullExcessOverMinimum</c> mode.</param>
    /// <param name="benefitPeriod">Benefit period (calendar date) the payment applies to.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The computed plan; never null on success.</returns>
    Task<Result<ExecutoryDocumentWithholdingPlanDto>> CalculateWithholdingsAsync(
        string debtorIdnp,
        decimal grossBenefitMdl,
        decimal legalMinimumMdl,
        DateOnly benefitPeriod,
        CancellationToken ct = default);
}

/// <summary>
/// R1406 / TOR §3.6-G — convenience applier wired between the unemployment-
/// benefit (indemnizație șomaj) payment pipeline and the executory-document
/// registry. Computes the withholding plan, returns the NET payable amount,
/// and writes the per-row tally updates after the payment is committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring contract.</b> The upstream service MUST call
/// <see cref="ComputePlanAsync"/> before disbursement to learn the NET
/// payable amount, then call <see cref="CommitPlanAsync"/> after the payment
/// is committed to update the per-row <c>TotalWithheldMdl</c> tallies. The
/// two calls are intentionally split — committing before disbursement would
/// double-count when the payment fails and is reissued.
/// </para>
/// <para>
/// <b>Audit + metric.</b> Each non-zero allocation produces a <c>EXECUTORY_DOC.WITHHELD</c>
/// audit row at Information severity and increments the
/// <c>cnas.executory_doc.withholding_applied</c> counter (tagged with
/// <c>priority_rank</c>).
/// </para>
/// </remarks>
public interface IUnemploymentBenefitWithholdingApplier
{
    /// <summary>
    /// R1406 — computes the per-payment withholding plan for the supplied
    /// debtor and unemployment-benefit amount. Pure computation — call
    /// <see cref="CommitPlanAsync"/> separately to commit the result.
    /// </summary>
    /// <param name="debtorIdnp">IDNP of the beneficiary.</param>
    /// <param name="grossBenefitMdl">Gross unemployment benefit (MDL).</param>
    /// <param name="legalMinimumMdl">Legal minimum-subsistence floor (MDL) — sourced from configuration / classifier.</param>
    /// <param name="benefitPeriod">Calendar date the payment applies to.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The plan on success; failure result otherwise.</returns>
    Task<Result<ExecutoryDocumentWithholdingPlanDto>> ComputePlanAsync(
        string debtorIdnp,
        decimal grossBenefitMdl,
        decimal legalMinimumMdl,
        DateOnly benefitPeriod,
        CancellationToken ct = default);

    /// <summary>
    /// R1406 — commits a previously-computed plan to the registry: writes
    /// the per-row <c>TotalWithheldMdl</c> update + emits the audit row +
    /// increments the metric for every non-zero allocation. Idempotency is
    /// the caller's responsibility — supply a deterministic
    /// <paramref name="sourceReference"/> so duplicate calls are detectable
    /// in the audit trail.
    /// </summary>
    /// <param name="plan">Plan returned by <see cref="ComputePlanAsync"/>.</param>
    /// <param name="sourceReference">Stable reference identifying the upstream payment (e.g. <c>UNEMPLOYMENT.PAYMENT.{paymentSqid}</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Success when every row was committed; failure on the first non-zero row that fails.</returns>
    Task<Result> CommitPlanAsync(
        ExecutoryDocumentWithholdingPlanDto plan,
        string sourceReference,
        CancellationToken ct = default);
}
