namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — change-traceable postal address attached to a
/// <see cref="Contributor"/> (the legal-person "Plătitor"/"Contribuabil"). Every address
/// mutation supersedes the previous current row by stamping <see cref="ValidToUtc"/>
/// and inserting a new row with <see cref="ValidFromUtc"/> equal to the supersession
/// instant. The application layer enforces "exactly one current row per parent" via
/// the filtered unique index (<c>WHERE ValidToUtc IS NULL</c>) configured in
/// <c>PayerAddressConfiguration</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a child table.</b> Earlier revisions stored a flat <c>PostalAddress</c>
/// column on <see cref="Contributor"/>; the ARH 028 traceability requirement forces
/// a historical model so an investigator can answer "what address was on file when
/// decision X was rendered" without depending on the audit log alone.
/// </para>
/// <para>
/// <b>Supersession pattern</b> mirrors R0129 versioning: a non-destructive UPDATE
/// (the current row is closed by setting <see cref="ValidToUtc"/>) plus an INSERT
/// of the new row. The doubly-linked chain is implied by the <see cref="ValidFromUtc"/>
/// ordering — no FK pointer back to the predecessor is needed because the query
/// "current row at instant T" is served by
/// <c>WHERE ValidFromUtc &lt;= T AND (ValidToUtc IS NULL OR ValidToUtc &gt; T)</c>.
/// </para>
/// </remarks>
public sealed class PayerAddress : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>Street line — building number + street name. 1..200 chars.</summary>
    public required string Street { get; set; }

    /// <summary>City / town. 1..200 chars.</summary>
    public required string City { get; set; }

    /// <summary>Region (raion / county). 1..200 chars.</summary>
    public required string Region { get; set; }

    /// <summary>Postal code. 4..10 alphanumeric.</summary>
    public required string PostalCode { get; set; }

    /// <summary>ISO-3166-1 alpha-2 country code (e.g. <c>MD</c>). Default is <c>MD</c>.</summary>
    public string Country { get; set; } = "MD";

    /// <summary>UTC instant at which this row became the active address. Required.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. <c>null</c> means still current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change, captured at supersession time. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// Sqid string of the operator who recorded the change. Stable opaque reference
    /// for audit — we deliberately do NOT enforce an FK to <c>UserProfile</c> because
    /// the operator may have been deactivated/soft-deleted while the row remains
    /// historically meaningful.
    /// </summary>
    public string? RecordedByUserSqid { get; set; }
}
