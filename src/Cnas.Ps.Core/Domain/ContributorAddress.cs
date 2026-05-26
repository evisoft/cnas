namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — change-traceable postal address attached to an
/// <see cref="InsuredPerson"/> (Persoană asigurată). Supersession semantics mirror
/// <see cref="PayerAddress"/>: every mutation closes the current row
/// (<see cref="ValidToUtc"/> = now) and inserts a new row (<see cref="ValidFromUtc"/> =
/// now). Filtered unique index on <c>(ContributorId) WHERE ValidToUtc IS NULL</c>
/// enforces "exactly one current address row per Contributor".
/// </summary>
/// <remarks>
/// Naming note: the entity-name prefix is <c>Contributor*</c> matching the R0311
/// brief; the FK column is <see cref="ContributorId"/>; both point at the natural-
/// person <see cref="InsuredPerson"/> row (the codebase's existing name for
/// "Persoană asigurată" — see the in-system entity for the rationale). The brief's
/// R0301-vs-R0311 distinction is preserved by routing R0301 child tables to
/// <c>Contributor</c> (Plătitor / legal person) and R0311 child tables here to
/// <c>InsuredPerson</c> (natural person).
/// </remarks>
public sealed class ContributorAddress : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> (Persoană asigurată) row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Street line. 1..200 chars.</summary>
    public required string Street { get; set; }

    /// <summary>City / town. 1..200 chars.</summary>
    public required string City { get; set; }

    /// <summary>Region (raion / county). 1..200 chars.</summary>
    public required string Region { get; set; }

    /// <summary>Postal code. 4..10 alphanumeric.</summary>
    public required string PostalCode { get; set; }

    /// <summary>ISO-3166-1 alpha-2 country code (default <c>MD</c>).</summary>
    public string Country { get; set; } = "MD";

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. Null when current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — address-kind discriminator. <c>Legal</c> = registered legal
    /// seat; <c>Postal</c> = mailing address; <c>Office</c> = physical office location. Optional
    /// for backward compatibility with existing rows (legacy rows default to <c>Postal</c> when
    /// interpreted by the registry browser).
    /// </summary>
    public ContributorAddressKind? AddressKind { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — building number component of the street address (e.g. <c>"42"</c>
    /// or <c>"42A"</c>). PII; encrypted at rest via <c>EncryptedStringConverter</c>. Capped at 32
    /// chars by the EF configuration.
    /// </summary>
    public string? BuildingNumber { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — apartment / suite component of the street address (e.g.
    /// <c>"15"</c> or <c>"3A"</c>). PII; encrypted at rest via <c>EncryptedStringConverter</c>.
    /// Capped at 32 chars by the EF configuration.
    /// </summary>
    public string? Apartment { get; set; }
}

/// <summary>
/// R0805 / Annex 1 §8.1.1.6 — kind discriminator for a Contributor's address rows.
/// </summary>
public enum ContributorAddressKind
{
    /// <summary>Registered legal seat (sediu juridic).</summary>
    Legal = 0,

    /// <summary>Mailing address (adresă poștală) — default.</summary>
    Postal = 1,

    /// <summary>Physical office / branch location (sediu fizic).</summary>
    Office = 2,
}
