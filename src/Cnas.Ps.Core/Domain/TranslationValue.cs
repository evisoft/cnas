namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — per-language localised text for a
/// <see cref="TranslationKey"/>. One row per (key, language) pair; the resolver picks
/// the row whose <see cref="Language"/> matches the caller's preference and falls
/// back to the RO row when the requested language is missing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on
/// (<see cref="TranslationKeyId"/>, <see cref="Language"/>) — the upsert service
/// applies this invariant idempotently and the database unique index is the safety
/// net against duplicate inserts.
/// </para>
/// <para>
/// <b>Approval flow.</b> <see cref="IsApproved"/> starts as <c>false</c> when an
/// operator drafts a new translation. A reviewer flips it to <c>true</c> via the
/// dedicated approve endpoint, which emits a Critical
/// <c>TRANSLATION.APPROVED</c> audit row. Unapproved drafts ARE still returned by
/// the resolver — the flag is informational so operators can spot strings awaiting
/// review without hiding them from production. The audit trail makes review activity
/// auditable end-to-end.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the
/// approve endpoint addresses values by their Sqid-encoded id; mutating endpoints
/// (upsert) address values by the natural (key Sqid + language) pair instead.
/// </para>
/// </remarks>
public sealed class TranslationValue : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the <see cref="TranslationKey"/> this value localises. The composite
    /// UNIQUE on (<see cref="TranslationKeyId"/>, <see cref="Language"/>) plus the
    /// FK guarantees the row's identity within the registry.
    /// </summary>
    public long TranslationKeyId { get; set; }

    /// <summary>
    /// ISO-639-1 lowercase language code restricted to the canonical CNAS set —
    /// <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>. The validator enforces the allow-list
    /// and the EF column caps at 8 characters.
    /// </summary>
    public required string Language { get; set; }

    /// <summary>
    /// Localised text rendered to the user. Capped at 2000 characters at the EF
    /// mapping layer — long-form copy (multi-paragraph emails) belongs in the
    /// template-variants registry (R0133), not here.
    /// </summary>
    public required string Text { get; set; }

    /// <summary>
    /// Review flag. <c>false</c> on initial draft; flipped to <c>true</c> by the
    /// dedicated approve endpoint which emits a Critical <c>TRANSLATION.APPROVED</c>
    /// audit row. The resolver does NOT filter on this flag — operators can ship
    /// drafts without losing the audit signal on who approved what and when.
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Optional free-form note left by the translator for the reviewer (e.g. "kept
    /// the Russian masculine because the addressee is always the head of household").
    /// Capped at 1024 characters at the EF mapping layer; nullable.
    /// </summary>
    public string? TranslatorNote { get; set; }
}
