namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0921 / TOR BP 2.3-B / Annex 2 §8.2.4 — pre-01.01.1999 activity period for a
/// natural-person <see cref="Solicitant"/>. One row per employment / labour period
/// digitised from the citizen's paper Carnet de muncă (cf. <see cref="LaborBooklet"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner.</b> The period belongs to a natural-person
/// <see cref="Solicitant"/> (FK <see cref="InsuredPersonSolicitantId"/>) rather
/// than the <see cref="InsuredPerson"/> aggregate because the citizen may walk
/// in to register pre-1999 history BEFORE the <see cref="InsuredPerson"/> row
/// has been provisioned. This mirrors the parallel decision made for
/// <see cref="LaborBooklet"/> and supersedes the older
/// <see cref="ContributorPre1999PeriodCarnetMunca"/> entity (R0311) which
/// attached to the <see cref="Contributor"/> aggregate (employer payer)
/// — a structural mismatch for the pre-1999 use-case.
/// </para>
/// <para>
/// <b>Booklet linkage.</b> The optional <see cref="LaborBookletId"/> FK points
/// at the digitised <see cref="LaborBooklet"/> row that sourced the period.
/// Typically populated; null in edge cases where a citizen reports a period
/// from a no-longer-extant booklet (e.g. lost / destroyed paper original).
/// </para>
/// <para>
/// <b>Date bound.</b> The validator enforces <c>PeriodEndDate &lt;= 1998-12-31</c>
/// at the boundary — the 01.01.1999 transition is the cut-off above which the
/// regular contribution-declarations pipeline (R0810-R0823) is the authoritative
/// source.
/// </para>
/// <para>
/// <b>Supersession.</b> The standard R0301-style change-traceability columns
/// (<see cref="ValidFromUtc"/>, <see cref="ValidToUtc"/>,
/// <see cref="ChangeReason"/>, <see cref="RecordedByUserSqid"/>) let the
/// application service emit a fresh row + close the previous one rather than
/// editing in place — so the historical trail survives every amend operation.
/// </para>
/// </remarks>
public sealed class InsuredPersonPre1999Period : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the owning <see cref="Solicitant"/> primary key (the natural-person
    /// applicant). Indexed via the composite index on
    /// (InsuredPersonSolicitantId, PeriodStartDate) configured in the EF
    /// configuration to back the per-citizen ascending-date listing endpoint.
    /// </summary>
    public long InsuredPersonSolicitantId { get; set; }

    /// <summary>
    /// FK to the sourcing <see cref="LaborBooklet"/> row when known. Optional —
    /// rare edge cases (lost / destroyed booklet) lack the linkage.
    /// </summary>
    public long? LaborBookletId { get; set; }

    /// <summary>Inclusive start date of the employment / activity period.</summary>
    public DateOnly PeriodStartDate { get; set; }

    /// <summary>
    /// Inclusive end date of the employment / activity period. Validator
    /// enforces <c>&lt;= 1998-12-31</c> at the boundary.
    /// </summary>
    public DateOnly PeriodEndDate { get; set; }

    /// <summary>Employer name as recorded in the booklet. Optional. Capped at 200 chars.</summary>
    public string? EmployerName { get; set; }

    /// <summary>Position / job title as recorded in the booklet. Optional. Capped at 200 chars.</summary>
    public string? Position { get; set; }

    /// <summary>
    /// Number of days worked within the period (0..366) when the paper booklet
    /// reports an explicit figure distinct from the inclusive date span.
    /// Optional; the validator caps the upper bound at 366 to cover leap years.
    /// </summary>
    public int? DaysWorked { get; set; }

    /// <summary>
    /// External reference to the supporting paper document (booklet page number,
    /// archive code, ...). Optional. Capped at 200 chars.
    /// </summary>
    public string? ProofDocumentReference { get; set; }

    /// <summary>
    /// Free-text annotation. Capped at 500 chars by the persistence configuration.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded; null while current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Operator-supplied rationale for the change (3..500 chars when set).</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator that recorded the entry.</summary>
    public string? RecordedByUserSqid { get; set; }
}
