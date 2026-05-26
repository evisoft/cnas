namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — change-traceable contact details attached to an
/// <see cref="InsuredPerson"/> (Persoană asigurată). Supersession-only updates;
/// filtered unique index on <c>(ContributorId) WHERE ValidToUtc IS NULL</c> enforces
/// the single-current-row invariant.
/// </summary>
public sealed class ContributorContact : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Primary phone in E.164 format.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Primary email used for correspondence.</summary>
    public string? Email { get; set; }

    /// <summary>Free-text contact-person name (e.g. relative authorised to communicate). Max 200 chars.</summary>
    public string? ContactPersonName { get; set; }

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. Null when current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — contact-kind discriminator. Distinguishes phone / email /
    /// fax channels so the registry can hold multiple current-row snapshots side by side in
    /// future schema revisions. Optional for backward compatibility with existing rows.
    /// </summary>
    public ContributorContactKind? ContactKind { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — kind-typed contact value (E.164 phone, RFC-5322 email, or
    /// fax number depending on <see cref="ContactKind"/>). PII; encrypted at rest via
    /// <c>EncryptedStringConverter</c>. Capped at 256 chars by the EF configuration.
    /// </summary>
    public string? Value { get; set; }
}

/// <summary>
/// R0805 / Annex 1 §8.1.1.6 — kind discriminator for a Contributor's contact rows.
/// </summary>
public enum ContributorContactKind
{
    /// <summary>Telephone number (E.164 format).</summary>
    Phone = 0,

    /// <summary>Email address (RFC-5322).</summary>
    Email = 1,

    /// <summary>Fax number (legacy).</summary>
    Fax = 2,
}
