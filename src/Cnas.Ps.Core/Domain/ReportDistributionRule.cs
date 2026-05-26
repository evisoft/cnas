namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1906 / TOR Annex 6 — per-report distribution rule. Captures WHO receives
/// a particular report code and THROUGH WHICH CHANNEL when a finalised
/// <c>ReportRun</c> is fanned out by the
/// <c>Cnas.Ps.Application.Reporting.IReportDistributionDispatcher</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Rules are CRUD-managed by administrators through the
/// <c>cnas-admin</c> REST surface. Soft-deletion (<see cref="AuditableEntity.IsActive"/>)
/// hides the rule from the dispatcher without losing the audit history.
/// The (<see cref="EffectiveFrom"/>, <see cref="EffectiveUntil"/>) window
/// is checked against the dispatch instant on every fan-out so an admin
/// can schedule a rule to "switch on" at a future date without flipping
/// <c>IsActive</c>.
/// </para>
/// <para>
/// <b>Encryption at rest.</b> <see cref="RecipientCode"/> is encrypted at
/// rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7). Equality
/// lookups on email addresses are restored through the
/// <see cref="RecipientCodeHash"/> shadow column maintained by the service
/// layer via <c>IDeterministicHasher</c>. For non-email recipient kinds
/// (User / Group / Role / MNotifyCategory) the hash column is NULL — the
/// underlying value is already a small opaque code that the service layer
/// can compare directly post-decryption.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ReportDistributionRuleDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII in logs.</b> The dispatcher and the channel handlers MUST
/// never log <see cref="RecipientCode"/> verbatim — the value is treated
/// as PII for the EmailAddress case and as internal metadata for the
/// others. Audit rows reference rules by their primary-key id.
/// </para>
/// </remarks>
public sealed class ReportDistributionRule : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable report code identifying the report whose runs this rule
    /// distributes (e.g. <c>ACCESS_RIGHTS.FULL_MATRIX</c>,
    /// <c>INTEGRITY_CHECK.NIGHTLY_SUMMARY</c>,
    /// <c>TREASURY.DAILY_DISTRIBUTION</c>). Validated against the
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c> regex; capped at 64 characters.
    /// </summary>
    public required string ReportCode { get; set; }

    /// <summary>Channel through which the report is delivered (in-system / dashboard / email / MNotify).</summary>
    public ReportDistributionChannel Channel { get; set; }

    /// <summary>Semantic kind of <see cref="RecipientCode"/> — user / group / role / email / MNotify category.</summary>
    public ReportRecipientKind RecipientKind { get; set; }

    /// <summary>
    /// Encrypted-at-rest recipient address. The interpretation depends on
    /// <see cref="RecipientKind"/>:
    /// <list type="bullet">
    ///   <item><c>User</c> → user Sqid or login.</item>
    ///   <item><c>Group</c> → group code.</item>
    ///   <item><c>Role</c> → role code.</item>
    ///   <item><c>EmailAddress</c> → raw email (PII).</item>
    ///   <item><c>MNotifyCategory</c> → MNotify category code.</item>
    /// </list>
    /// Capped at 256 characters at the column layer.
    /// </summary>
    public required string RecipientCode { get; set; }

    /// <summary>
    /// HMAC-SHA256 shadow of the canonicalised email (lower-case, trimmed)
    /// populated ONLY when <see cref="RecipientKind"/> is
    /// <see cref="ReportRecipientKind.EmailAddress"/>. Null for every other
    /// recipient kind. Backs the unique-index equality lookup so two rules
    /// cannot duplicate the same (report, channel, kind, email) tuple even
    /// though the email column itself is non-deterministic ciphertext.
    /// 44-char base64 (HMAC-SHA256).
    /// </summary>
    public string? RecipientCodeHash { get; set; }

    /// <summary>Desired delivery format (PDF / CSV / XLSX / LinkOnly).</summary>
    public ReportDeliveryFormat Format { get; set; }

    /// <summary>Urgency tag forwarded to channels that support per-message priority.</summary>
    public ReportDeliveryPriority Priority { get; set; }

    /// <summary>First date (UTC midnight) the rule applies. Inclusive.</summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Last date the rule applies (inclusive). Null means open-ended.</summary>
    public DateOnly? EffectiveUntil { get; set; }

    /// <summary>FK to the <c>UserProfile</c> who created the rule.</summary>
    public long CreatedByUserId { get; set; }

    /// <summary>Optional free-form note (capped at 1000 chars). Captured for operator context.</summary>
    public string? Notes { get; set; }
}
