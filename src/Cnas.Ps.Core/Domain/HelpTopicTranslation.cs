namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0225 / TOR UI 015 — per-language localised title + body for a
/// <see cref="HelpTopic"/>. One row per (topic, language) pair; the resolver returns
/// the topic with every available translation so the UI can pick the caller's
/// preference (or fall back when missing).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on
/// (<see cref="HelpTopicId"/>, <see cref="Language"/>) — the upsert service applies
/// this invariant idempotently and the database unique index is the safety net.
/// </para>
/// <para>
/// <b>Body format.</b> <see cref="BodyMarkdown"/> is Markdown. The UI renders it
/// through a sandboxed Markdown→HTML pipeline so operators can use headings, lists
/// and inline emphasis without authoring raw HTML. Capped at 20_000 characters at
/// the EF mapping layer — the help widget is for contextual tips, not encyclopedia
/// entries.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the
/// admin REST surface addresses individual translations by their Sqid-encoded id
/// when the operator approves a per-language draft.
/// </para>
/// </remarks>
public sealed class HelpTopicTranslation : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the <see cref="HelpTopic"/> this translation localises. The composite
    /// UNIQUE on (<see cref="HelpTopicId"/>, <see cref="Language"/>) plus the FK
    /// guarantees the row's identity within the registry.
    /// </summary>
    public long HelpTopicId { get; set; }

    /// <summary>
    /// ISO-639-1 lowercase language code restricted to the canonical CNAS set —
    /// <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>. The validator enforces the allow-list
    /// and the EF column caps at 8 characters.
    /// </summary>
    public required string Language { get; set; }

    /// <summary>
    /// Tooltip / dialog title rendered above the body. Capped at 200 characters at
    /// the EF mapping layer.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Markdown body rendered by the sandboxed UI pipeline. Capped at 20_000
    /// characters at the EF mapping layer.
    /// </summary>
    public required string BodyMarkdown { get; set; }

    /// <summary>
    /// Review flag. <c>false</c> on initial draft; flipped to <c>true</c> by an
    /// admin approve flow (deferred — the contextual-help widget surfaces drafts
    /// directly so authors can see their copy without a separate publish step).
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Optional free-form note left by the translator for the reviewer. Capped at
    /// 1024 characters at the EF mapping layer; nullable.
    /// </summary>
    public string? TranslatorNote { get; set; }
}
