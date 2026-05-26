namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2282 / TOR SEC 036 — one row-integrity invariant violation recorded by an
/// <see cref="IntegrityCheckRun"/>. Operators triage findings through the
/// admin dashboard and acknowledge them once investigated.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "finding" means.</b> A finding represents a row whose stored state
/// no longer satisfies a documented invariant — examples include a Claim
/// whose <c>PaidAmount</c> diverges from the sum of its child payments, an
/// ExecutoryDocument that has withheld more than the total owed, or a
/// UserProfile with a NationalId but no NationalIdHash shadow.
/// </para>
/// <para>
/// <b>Why a raw row id.</b> <see cref="AggregateRowId"/> is the offending
/// row's RAW database PK, not a Sqid. Findings are an internal ops surface —
/// operators MUST be able to use the value to locate the offending row in
/// dump tools, ad-hoc SQL, and the EF Designer without first round-tripping
/// through <c>ISqidService</c>. The companion <c>AggregateName</c> column
/// disambiguates which table the id refers to. This exception is documented
/// in the <c>IntegrityCheckFindingDto</c> XML doc as well.
/// </para>
/// <para>
/// <b>Acknowledgement.</b> An operator may acknowledge a finding once
/// investigated. The acknowledgement carries a note (free-form, 3..1000
/// chars) and the acknowledging user's id. The base aggregate-row may still
/// be broken — acknowledgement is operator-bookkeeping, not a "fix".
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.IntegrityCheckFindingDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class IntegrityCheckFinding : AuditableEntity, IExternalId
{
    /// <summary>FK to the owning <see cref="IntegrityCheckRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>
    /// Stable, screaming-snake-case code identifying the failed invariant
    /// (e.g. <c>CLAIM.RUNNING_TOTAL_MISMATCH</c>,
    /// <c>EXECUTORY_DOC.WITHHOLDING_OVERFLOW</c>). Capped at 64 characters.
    /// </summary>
    public required string CheckCode { get; set; }

    /// <summary>Severity of the violation; drives ops paging and dashboard sort order.</summary>
    public IntegrityFindingSeverity Severity { get; set; }

    /// <summary>
    /// Display name of the offending aggregate (e.g. <c>Claim</c>,
    /// <c>ExecutoryDocument</c>, <c>UserProfile</c>). Capped at 128
    /// characters. Backed by an index together with <see cref="AggregateRowId"/>.
    /// </summary>
    public required string AggregateName { get; set; }

    /// <summary>
    /// Raw bigint PK of the offending row. Documented as a raw id (NOT
    /// Sqid-encoded) because findings are an internal ops surface and
    /// operators must be able to reach the row via dump tools / ad-hoc SQL.
    /// </summary>
    public long AggregateRowId { get; set; }

    /// <summary>
    /// Human-readable description of the violation. Capped at 1000 characters.
    /// MUST NEVER contain PII (IDNP, IDNO, IBAN, personal name) — refer to
    /// rows via <see cref="AggregateRowId"/> only.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Expected value reported by the invariant rule, when known (e.g. the
    /// recomputed sum). Capped at 256 characters. Null when the rule does not
    /// publish an expectation.
    /// </summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// Actual value observed at scan time, when known. Capped at 256 characters.
    /// Null when the rule does not publish an observation.
    /// </summary>
    public string? ActualValue { get; set; }

    /// <summary>UTC timestamp the finding was inserted (matches <see cref="AuditableEntity.CreatedAtUtc"/>).</summary>
    public DateTime FirstDetectedAt { get; set; }

    /// <summary>Whether an operator has acknowledged the finding.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>UTC timestamp of the acknowledgement, when applicable.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> who acknowledged the finding.</summary>
    public long? AcknowledgedByUserId { get; set; }

    /// <summary>
    /// Free-form note accompanying the acknowledgement (3..1000 chars when
    /// set; null while the finding is unacknowledged).
    /// </summary>
    public string? AcknowledgementNote { get; set; }
}
