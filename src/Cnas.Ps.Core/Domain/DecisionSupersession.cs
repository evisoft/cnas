using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0933 / TOR §3.6 §10.1 — append-only audit row linking a newly accepted
/// decision to the prior active decision it superseded for the same
/// (Solicitant, ServiceCode) pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate aggregate.</b> The terminate-prior lifecycle (R0933) is a
/// cross-aggregate operation: it mutates the prior <see cref="ServiceApplication"/>
/// (status → <see cref="ApplicationStatus.Closed"/>) AND records the link from
/// the new decision back to the prior one. The link itself is interesting for
/// regression-period reporting (how often does a higher pension supersede a
/// lower one?), for the citizen-facing "your previous decision X was terminated
/// because we issued the new decision Y" notification (R0940), and for the
/// downstream payment-pipeline (R0938) which uses the supersession row to know
/// which periodic disbursement schedule must be stopped.
/// </para>
/// <para>
/// <b>Append-only.</b> Rows are never updated or hard-deleted. Each new
/// acceptance event produces exactly one new row pointing at the prior
/// decision it replaces; if the new decision is itself superseded later, a
/// second row is appended (<c>PreviousDecisionId</c> = the second decision,
/// <c>NewDecisionId</c> = the third).
/// </para>
/// <para>
/// <b>Natural key.</b> The pair (<see cref="PreviousDecisionId"/>,
/// <see cref="NewDecisionId"/>) is unique — the same prior decision cannot be
/// terminated twice through this surface. The unique index lives on the EF
/// configuration.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.DecisionSupersessionDto.Id</c>) carries
/// a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "DECISION_SUPERSESSION")]
public sealed class DecisionSupersession : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the previously active <see cref="ServiceApplication"/> (decision)
    /// that was terminated when the new decision landed. Indexed.
    /// </summary>
    public long PreviousDecisionId { get; set; }

    /// <summary>
    /// FK to the newly accepted <see cref="ServiceApplication"/> (decision)
    /// that caused the prior decision to terminate. Indexed.
    /// </summary>
    public long NewDecisionId { get; set; }

    /// <summary>
    /// UTC instant the supersession was recorded. Distinct from the prior
    /// decision's <see cref="ServiceApplication.ClosedAtUtc"/> only when a
    /// retry or back-fill scenario reattaches the link — the audit trail keeps
    /// both timestamps.
    /// </summary>
    public DateTime SupersededAtUtc { get; set; }

    /// <summary>
    /// Internal user id of the actor that triggered the supersession (typically
    /// the CnasDecider who approved the new decision). Null when the
    /// supersession is system-initiated (e.g. proactive sweep).
    /// </summary>
    public long? SupersededByUserId { get; set; }

    /// <summary>
    /// Optional free-text rationale (≤ 500 chars). Common values: the prior
    /// decision's reference number, the warning-acknowledged-by-decider flag
    /// when the new sum is lower than the prior sum, the proactive-sweep job
    /// run id.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Snapshot of the prior decision's <c>Amount</c> at supersession time
    /// (MDL). Captured because the underlying <see cref="ServiceApplication"/>
    /// row carries only the form payload; this column lets the comparison
    /// surface (R0933) answer "what was the delta?" without joining back to
    /// the form JSON.
    /// </summary>
    public decimal? PriorAmount { get; set; }

    /// <summary>
    /// Snapshot of the new decision's <c>Amount</c> at supersession time
    /// (MDL). See <see cref="PriorAmount"/> for the storage rationale.
    /// </summary>
    public decimal? NewAmount { get; set; }
}
