namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Solicitant (applicant) — natural or legal person who interacts with SI PS to obtain
/// social-protection services. See TOR §2.2 (Solicitant role) and §2.3 (information objects).
/// </summary>
/// <remarks>
/// The Solicitant entity holds presentation/contact data only — substantive person data
/// (IDNP, IDNO) comes from RSP / RSUD via MConnect per CLAUDE.md unique-address principle.
/// </remarks>
public sealed class Solicitant : AuditableEntity, IExternalId
{
    /// <summary>National identifier — IDNP for natural persons, IDNO for legal persons.</summary>
    /// <remarks>
    /// <para>
    /// UTF-8 STRING(13) per ARH 015. Always stored as string to preserve leading zeros.
    /// </para>
    /// <para>
    /// Encrypted at rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7 / TOR SEC 035).
    /// Because every encryption samples a fresh nonce, equality lookups against this column
    /// (<c>WHERE NationalId == X</c>) cease to work in production; use the
    /// <see cref="NationalIdHash"/> shadow column instead — see that property's remarks
    /// for the synchronization contract.
    /// </para>
    /// </remarks>
    public required string NationalId { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 of the canonicalized <see cref="NationalId"/>. Backs the
    /// unique index and equality lookups that the encrypted plaintext column can no longer
    /// support (per the converter remarks above). Stored as base64 (44 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronization is the application layer's responsibility.</b> Every site that
    /// writes <see cref="NationalId"/> MUST also write this column via
    /// <c>Cnas.Ps.Application.Abstractions.IDeterministicHasher.ComputeHash</c> on
    /// the same value. We do NOT compute this in an EF interceptor because:
    /// (a) interceptors create silent coupling between the entity and DI that confuses
    /// debugging, and (b) the canonicalization rule lives in <c>IDeterministicHasher</c>
    /// (a single source of truth) — duplicating it inside an interceptor would create
    /// a second canonicalizer that could drift out of sync with the first.
    /// </para>
    /// <para>
    /// Default is <see cref="string.Empty"/> so test factories that construct
    /// <see cref="Solicitant"/> aggregates in-memory (without exercising hash-driven
    /// lookups) do not have to set it explicitly. Production code paths
    /// (<c>SolicitantService</c>, <c>UserDirectoryService</c>, MConnect sync jobs) MUST
    /// populate it before <c>SaveChanges</c>.
    /// </para>
    /// </remarks>
    public string NationalIdHash { get; set; } = string.Empty;

    /// <summary>Classification — natural or legal person.</summary>
    public ApplicantKind Kind { get; set; }

    /// <summary>Display name (full name for natural persons, denumire for legal persons).</summary>
    public required string DisplayName { get; set; }

    /// <summary>Primary email used for notifications (MNotify channel).</summary>
    public string? Email { get; set; }

    /// <summary>Primary phone (E.164) used for SMS notifications via MNotify.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Preferred UI language (BCP-47): <c>ro</c>, <c>en</c>, <c>ru</c>.</summary>
    public string PreferredLanguage { get; set; } = "ro";

    /// <summary>Postal address — captured for paper-mailing of decisions when applicable.</summary>
    public string? PostalAddress { get; set; }

    /// <summary>
    /// IDNP/IDNO of the legal entity this Solicitant belongs to (employer for natural persons,
    /// owner organization for legal persons). Populated from RSUD when known.
    /// </summary>
    public string? AffiliatedLegalEntityId { get; set; }

    /// <summary>
    /// Beneficiary IBAN used to disburse approved benefits via MPay. Captured on the
    /// application form when a paid service is requested. Validated upstream against
    /// the Moldovan IBAN rules — stored as the canonical UPPERCASE form per ISO-13616.
    /// </summary>
    public string? BankIban { get; set; }

    /// <summary>
    /// R0671 / TOR CF 18.06 — stable short region code (e.g. <c>"CHIS"</c>, <c>"BLT"</c>,
    /// <c>"BAL"</c>) used by the access-scope filter to narrow registry results to the
    /// caller's assigned geography. <c>null</c> on rows that pre-date scoping or on
    /// "national" Solicitants with no specific region (visible to every scoped caller per
    /// the <c>Cnas.Ps.Application.Abstractions.IAccessScope</c> NULL-data semantics; the
    /// cref is a plain string because Core may not reference Application).
    /// Capped at 16 chars at the persistence layer.
    /// </summary>
    public string? RegionCode { get; set; }
}
