namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1600 / TOR Annex 3.8 / R1406 §3.6-G — one executory document (document
/// executoriu) registered against a debtor's IDNP. Drives the withholding the
/// payment-dispatcher must apply to any benefit payable to the debtor (pension,
/// unemployment-allowance, indemnizație, ...).
/// </summary>
/// <remarks>
/// <para>
/// <b>Domain context.</b> Per art. 156 Codul Muncii a court / bailiff / notary
/// may compel an employer or social-protection authority to withhold a portion
/// of a beneficiary's income to satisfy an outstanding debt (child support,
/// civil judgments, administrative fines, ...). CNAS maintains the registry
/// of all such instruments that target benefit recipients and the payment-
/// dispatcher consults it before disbursement.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Documents land in <see cref="ExecutoryDocumentStatus.Active"/>
/// on registration. The operator may transition them to
/// <see cref="ExecutoryDocumentStatus.Suspended"/> (court appeal, hearing) and
/// back to Active. Two terminal states exist:
/// <see cref="ExecutoryDocumentStatus.Completed"/> (debt fully repaid — the
/// service auto-flips when <see cref="TotalWithheldMdl"/> reaches
/// <see cref="TotalOwedMdl"/>) and
/// <see cref="ExecutoryDocumentStatus.Cancelled"/> (document revoked — carries
/// a rationale).
/// </para>
/// <para>
/// <b>Encrypted columns.</b> <see cref="DebtorIdnp"/> and
/// <see cref="CreditorAccountIban"/> are PII / financially-sensitive and are
/// encrypted at rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7).
/// Equality lookups against the encrypted plaintext are unsupported; callers
/// query through the deterministic-hash shadow columns
/// <see cref="DebtorIdnpHash"/> / <see cref="CreditorAccountIbanHash"/> which
/// the application layer maintains via <c>IDeterministicHasher</c>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ExecutoryDocumentDto.Id</c>) carries a
/// Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class ExecutoryDocument : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable external identifier of the document (e.g. <c>EXE-2026-000123</c>
    /// or the court's own series-number like <c>OE-2026-1234</c>). Unique
    /// across the registry — the service may auto-generate the value when the
    /// caller does not supply one. Capped at 32 characters at the persistence
    /// layer.
    /// </summary>
    public required string DocumentSeriesNumber { get; set; }

    /// <summary>
    /// Moldovan personal-numeric-identifier (IDNP — 13 digits) of the debtor
    /// from whose benefit payments amounts must be withheld. Encrypted at rest
    /// per CLAUDE.md §5.7; equality lookups go through
    /// <see cref="DebtorIdnpHash"/>.
    /// </summary>
    public required string DebtorIdnp { get; set; }

    /// <summary>
    /// Base64-encoded HMAC-SHA256 hash of the canonicalised <see cref="DebtorIdnp"/>
    /// (44 chars including <c>=</c> padding). Backs equality lookups and the
    /// secondary index used by the withholding calculator. Maintained by the
    /// application layer — every site that writes <see cref="DebtorIdnp"/> MUST
    /// also write this column via <c>IDeterministicHasher.ComputeHash</c> on
    /// the same value.
    /// </summary>
    public required string DebtorIdnpHash { get; set; }

    /// <summary>Classification of the executory instrument (court / bailiff / notary / administrative / other).</summary>
    public ExecutoryDocumentKind Kind { get; set; }

    /// <summary>Lifecycle status. Drives whether the calculator considers the row for withholding.</summary>
    public ExecutoryDocumentStatus Status { get; set; } = ExecutoryDocumentStatus.Active;

    /// <summary>
    /// Issuing body (court name, bailiff office, notary office). Free-form text
    /// up to 256 characters.
    /// </summary>
    public required string IssuedBy { get; set; }

    /// <summary>Calendar date the document was issued. Must be ≤ today at validation time.</summary>
    public DateOnly IssuedDate { get; set; }

    /// <summary>
    /// First date on which withholding must occur. Must be ≥
    /// <see cref="IssuedDate"/>.
    /// </summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>
    /// Optional last date on which withholding must occur. Null = open-ended
    /// (perpetual child-support, ...). When set must be ≥
    /// <see cref="EffectiveFrom"/>.
    /// </summary>
    public DateOnly? EffectiveUntil { get; set; }

    /// <summary>
    /// Mode the calculator uses to translate this document into a per-payment
    /// withholding amount. See <see cref="ExecutoryDocumentWithholdingMode"/>.
    /// </summary>
    public ExecutoryDocumentWithholdingMode WithholdingMode { get; set; }

    /// <summary>
    /// Fixed MDL amount withheld per payment when
    /// <see cref="WithholdingMode"/> = <see cref="ExecutoryDocumentWithholdingMode.FixedAmount"/>.
    /// Null otherwise.
    /// </summary>
    public decimal? WithholdingAmountMdl { get; set; }

    /// <summary>
    /// Percentage (0..70) of the gross benefit withheld per payment when
    /// <see cref="WithholdingMode"/> = <see cref="ExecutoryDocumentWithholdingMode.Percentage"/>.
    /// Null otherwise. Stored with two-decimal precision.
    /// </summary>
    public decimal? WithholdingPercentage { get; set; }

    /// <summary>
    /// Priority rank (1 = highest — child support; 5 = lowest — civil
    /// judgments). When multiple Active documents target the same debtor the
    /// calculator honours them in PriorityRank ASC order until the 70% cap
    /// (art. 156 CMP) is reached.
    /// </summary>
    public int PriorityRank { get; set; }

    /// <summary>
    /// Destination IBAN (canonical UPPERCASE MD format) where the withheld
    /// amount must be remitted (bailiff trust account, creditor account, ...).
    /// Encrypted at rest; equality lookups go through
    /// <see cref="CreditorAccountIbanHash"/>.
    /// </summary>
    public required string CreditorAccountIban { get; set; }

    /// <summary>
    /// Base64-encoded HMAC-SHA256 hash of the canonicalised
    /// <see cref="CreditorAccountIban"/>. Backs equality lookups and the
    /// secondary index. Maintained by the application layer in lock-step with
    /// the plaintext column.
    /// </summary>
    public required string CreditorAccountIbanHash { get; set; }

    /// <summary>
    /// Display name of the creditor (bailiff office, ex-spouse, plaintiff
    /// company, ...). Free-form text up to 256 characters; surfaced in
    /// audit reports.
    /// </summary>
    public required string CreditorName { get; set; }

    /// <summary>
    /// Total amount of debt owed (MDL). Null when the obligation is open-ended
    /// (perpetual child support, ...). When set, the service auto-completes
    /// the row once <see cref="TotalWithheldMdl"/> reaches this figure.
    /// </summary>
    public decimal? TotalOwedMdl { get; set; }

    /// <summary>
    /// Running tally of all amounts withheld against this document so far
    /// (MDL). Defaults to 0; the calculator + service updates this column on
    /// every <c>RecordWithholdingAsync</c> call.
    /// </summary>
    public decimal TotalWithheldMdl { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> who registered the document.</summary>
    public int RegisteredByUserId { get; set; }

    /// <summary>
    /// Date the document transitioned to
    /// <see cref="ExecutoryDocumentStatus.Completed"/>. Null while the document
    /// is in a non-terminal state.
    /// </summary>
    public DateOnly? CompletedDate { get; set; }

    /// <summary>
    /// Operator-supplied rationale recorded when the document transitions to
    /// <see cref="ExecutoryDocumentStatus.Cancelled"/> (3..500 chars when set;
    /// null otherwise).
    /// </summary>
    public string? CancellationReason { get; set; }
}
