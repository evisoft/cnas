namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0913 / TOR BP 2.2-D — single contribution adjustment attributed to one
/// insured person, sourced from a non-REV-5 supporting document (court
/// decision, audit / control report, individual social-insurance contract,
/// or "other"). Lands as a <see cref="PersonalAccountEntry"/> with the
/// document-code as the entry source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signed amount.</b> <see cref="AdjustmentAmount"/> is intentionally
/// signed — positive values add to the citizen's personal account, negative
/// values subtract. Bounded ±10_000_000 MDL by the validator.
/// </para>
/// <para>
/// <b>Stable source-document code.</b> <see cref="SourceDocumentCode"/> is a
/// stable string from a fixed allow-list (<c>"CourtDecision"</c>,
/// <c>"AdminControl"</c>, <c>"IndividualContract"</c>, <c>"Other"</c>) so
/// downstream consumers can switch on a self-describing label without owning
/// a numeric mapping. The same string is used as
/// <see cref="PersonalAccountEntry.SourceCode"/> for the projected entry —
/// see the service for the projection contract.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/>
/// because the outbound DTO surfaces a Sqid-encoded surrogate per
/// CLAUDE.md RULE 3 — operators reference an individual adjustment when
/// annotating or correcting it.
/// </para>
/// </remarks>
public sealed class InsuredPersonContributionAdjustment : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the target insured person — modelled as a
    /// <see cref="Solicitant"/> id because the personal-account aggregate
    /// (R0516) is owned by the Solicitant entity.
    /// </summary>
    public long InsuredPersonSolicitantId { get; set; }

    /// <summary>
    /// Calendar month the adjustment applies to. By convention the day
    /// component is always 1 — validators enforce <c>Day == 1</c>.
    /// </summary>
    public DateOnly Month { get; set; }

    /// <summary>
    /// Signed adjustment amount (MDL). Positive adds, negative subtracts.
    /// Validator clamps the absolute value to 10_000_000.
    /// </summary>
    public decimal AdjustmentAmount { get; set; }

    /// <summary>
    /// Stable document-source code from the fixed allow-list
    /// (<c>"CourtDecision"</c>, <c>"AdminControl"</c>,
    /// <c>"IndividualContract"</c>, <c>"Other"</c>). Used as
    /// <see cref="PersonalAccountEntry.SourceCode"/> on the projected entry.
    /// </summary>
    public required string SourceDocumentCode { get; set; }

    /// <summary>
    /// Optional external reference (court decision number, audit report id,
    /// individual contract id, …) — diagnostic context only. ≤ 128 chars.
    /// </summary>
    public string? SourceDocumentReference { get; set; }

    /// <summary>
    /// Optional operator-supplied rationale (≤ 500 chars when set).
    /// </summary>
    public string? Reason { get; set; }
}
