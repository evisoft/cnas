namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0803 / ARH 028 / TOR BP 1.1-D — change-traceable bank-account row attached to a
/// <see cref="Contributor"/> (Plătitor). Mirrors the supersession semantics of
/// <see cref="PayerAddress"/> / <see cref="PayerContact"/>: every mutation either
/// appends a new row (multiple non-primary accounts may coexist) or supersedes the
/// current primary row by stamping <see cref="ValidToUtc"/> on the prior primary and
/// inserting a fresh primary.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Iban"/> column is encrypted at rest via the application-level
/// <c>EncryptedStringConverter</c> (CLAUDE.md §5.7 / TOR SEC 035) — the same pattern
/// that protects <see cref="Solicitant.BankIban"/>. Equality lookups against the
/// ciphertext do not work (fresh nonce per encryption) so the
/// <see cref="IbanHash"/> shadow column holds the deterministic HMAC of the
/// canonicalised IBAN (uppercase, no spaces) and backs the per-Payer uniqueness rule
/// and the "do we already have this IBAN on file" lookup. Synchronisation is the
/// application layer's responsibility — every site that writes <see cref="Iban"/>
/// MUST also write <see cref="IbanHash"/> via
/// <c>Cnas.Ps.Application.Abstractions.IDeterministicHasher.ComputeHash</c> on the
/// canonicalised value (mirrors the <see cref="Contributor.IdnoHash"/> contract).
/// </para>
/// <para>
/// Filtered unique indexes (configured in <c>PayerBankAccountConfiguration</c>)
/// enforce two invariants at the database level on the Postgres provider:
/// (a) at most one current primary row per Payer
/// (<c>UX_PayerBankAccounts_CurrentPrimary</c>) and (b) the same IBAN may not appear
/// on two open rows for the same Payer (<c>UX_PayerBankAccounts_CurrentIban</c>).
/// The InMemory test provider ignores filtered indexes — the service layer enforces
/// the invariants programmatically there.
/// </para>
/// </remarks>
public sealed class PayerBankAccount : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>Display name carried on the account (1..200 chars). Free-text.</summary>
    public required string AccountHolderName { get; set; }

    /// <summary>
    /// IBAN in canonical form — uppercase, no spaces, ISO 13616 shape
    /// (<c>^[A-Z]{2}\d{2}[A-Z0-9]{1,30}$</c>, max 34 chars). Encrypted at rest
    /// via <c>EncryptedStringConverter</c>; see <see cref="IbanHash"/> for
    /// equality-lookup support. Never log raw IBAN values — audit details
    /// carry only the first 8 chars of <see cref="IbanHash"/>.
    /// </summary>
    public required string Iban { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 of the canonicalised <see cref="Iban"/>. Backs the
    /// filtered-unique IBAN index and equality lookups that the encrypted plaintext
    /// column can no longer support. Stored as base64 (44 chars). See the entity
    /// remarks for the synchronisation contract.
    /// </summary>
    public string IbanHash { get; set; } = string.Empty;

    /// <summary>Bank denumire / brand (free-text, 1..200 chars).</summary>
    public required string BankName { get; set; }

    /// <summary>
    /// BIC / SWIFT code identifying the holding bank
    /// (<c>^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$</c>; 8 or 11 chars).
    /// </summary>
    public required string BankBic { get; set; }

    /// <summary>
    /// True when this row is the Payer's current primary bank account — exactly
    /// one current primary row per Payer is allowed (filtered unique index).
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>ISO 4217 alpha-3 currency code (default <c>MDL</c>).</summary>
    public string Currency { get; set; } = "MDL";

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded / closed. Null when current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
