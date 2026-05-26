namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — change-traceable contact details attached to a
/// <see cref="Contributor"/> (the legal-person "Plătitor"). Mirrors the supersession
/// semantics of <see cref="PayerAddress"/>: every mutation closes the current row
/// (<see cref="ValidToUtc"/> = now) and inserts a new row (<see cref="ValidFromUtc"/> =
/// now). The application layer enforces "exactly one current row per Payer" via the
/// filtered unique index configured in <c>PayerContactConfiguration</c>.
/// </summary>
/// <remarks>
/// All three contact fields are optional individually — a Payer may have phone-only,
/// email-only, or both — but each row is interpreted as a single coherent snapshot of
/// the contact details on file at the supersession instant. Hashing of PII for audit
/// detail strings happens in the service layer; this entity stores the canonical values
/// in the clear (subject to standard field-encryption rules applied elsewhere).
/// </remarks>
public sealed class PayerContact : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>Primary phone in E.164 format (e.g. <c>+37322000000</c>).</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Primary email used for correspondence.</summary>
    public string? Email { get; set; }

    /// <summary>Free-text contact-person name (e.g. accountant on file). Max 200 chars.</summary>
    public string? ContactPersonName { get; set; }

    /// <summary>UTC instant at which this row became the active contact snapshot. Required.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. <c>null</c> means still current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change. See <see cref="PayerAddress.RecordedByUserSqid"/>.</summary>
    public string? RecordedByUserSqid { get; set; }
}
