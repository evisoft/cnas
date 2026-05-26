namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1202 / TOR §3.4-C — finalised decision outcome attached to a
/// <see cref="CapitalisedPaymentRequest"/>. One row per finalised computation;
/// multiple computations may run on a single request (e.g. re-compute after a
/// rate change) — only the latest row is "current" and that surface is
/// resolved via <c>ComputedAtUtc DESC</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII in the breakdown.</b> <see cref="ComputationBreakdownJson"/>
/// summarises the per-period factors (period index, survival probability,
/// discount factor, contribution) — IDNP / IDNO / full names are NEVER
/// embedded. The service is responsible for keeping the payload PII-free.
/// </para>
/// </remarks>
public sealed class CapitalisedPaymentDecision : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="CapitalisedPaymentRequest"/>.</summary>
    public long RequestId { get; set; }

    /// <summary>Decision outcome — <c>Approved</c> or <c>Rejected</c>.</summary>
    public CapitalisedPaymentDecisionStatus DecisionStatus { get; set; }

    /// <summary>UTC instant the computation was run (stamped by the orchestrator).</summary>
    public DateTime ComputedAtUtc { get; set; }

    /// <summary>
    /// Beneficiary age in years at the valuation date (2-decimal precision —
    /// fractional age improves mortality interpolation).
    /// </summary>
    public decimal EffectiveAgeYears { get; set; }

    /// <summary>
    /// Number of monthly periods covered by the computation. Equal to the
    /// month-span between valuation date and obligation end when the
    /// obligation is fixed-end; otherwise driven by the mortality table for
    /// the beneficiary's age and sex.
    /// </summary>
    public int LifeExpectancyMonths { get; set; }

    /// <summary>
    /// Monthly compounded effective discount factor — derived from the
    /// annual <see cref="CapitalisedPaymentRequest.LegalDiscountRatePercent"/>
    /// via <c>(1 + r/100)^(1/12) - 1</c>. Persisted with 8-decimal precision.
    /// </summary>
    public decimal EffectiveDiscountMonthly { get; set; }

    /// <summary>
    /// Computed present value of the future stream (MDL). 2-decimal precision;
    /// rounded with banker's rounding.
    /// </summary>
    public decimal CapitalisedAmountMdl { get; set; }

    /// <summary>
    /// JSON-encoded per-period breakdown captured at computation time —
    /// <c>{ t, survivalProb, discountFactor, contribution }</c> rows plus a
    /// header summary. Capped at 32_768 characters; for long horizons the
    /// service samples (first 24 + last 24 + every-12-month mid-range rows).
    /// </summary>
    public required string ComputationBreakdownJson { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> that approved the decision (null while pending or rejected).</summary>
    public int? ApprovedByUserId { get; set; }

    /// <summary>
    /// Operator-supplied rejection reason recorded when
    /// <see cref="DecisionStatus"/> = <see cref="CapitalisedPaymentDecisionStatus.Rejected"/>
    /// (3..1000 chars; null otherwise).
    /// </summary>
    public string? RejectionReason { get; set; }
}
