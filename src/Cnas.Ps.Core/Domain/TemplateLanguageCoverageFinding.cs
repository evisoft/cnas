namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2003 / R0133 — one persisted record that a <see cref="DocumentTemplate"/>
/// is missing a required-language variant (e.g. EN or RU under the canonical
/// RO/EN/RU set). The coverage scanner inserts one row per
/// (Template, MissingLanguage) gap; operators acknowledge a finding once the
/// translation is queued or the gap is otherwise resolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dedup contract.</b> The repository enforces a filtered unique index on
/// (<see cref="TemplateId"/>, <see cref="MissingLanguage"/>,
/// <see cref="Acknowledged"/>) WHERE <c>Acknowledged=false</c>. This means at
/// most ONE open finding can exist for a given template-language gap at any
/// moment; the next scan that observes the same gap returns the existing
/// open row instead of creating a duplicate. Acknowledging the open row
/// "closes" it; if the gap re-emerges (e.g. an approved variant is later
/// retracted) a fresh open row is inserted alongside the closed history.
/// </para>
/// <para>
/// <b>PII safety.</b> The row references only the template's surrogate id +
/// stable kebab-case code and the missing language code. No citizen data is
/// stored here — gaps are operational ops data.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.TemplateLanguageCoverageFindingDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class TemplateLanguageCoverageFinding : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="DocumentTemplate"/> the gap was detected on.</summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Lowercase ISO 639-1 / 639-2 code of the language the parent template
    /// is missing an approved variant for. Capped at 8 characters in the
    /// schema (matches the <c>TemplateVariant.Language</c> column width).
    /// </summary>
    public required string MissingLanguage { get; set; }

    /// <summary>UTC timestamp the gap was first detected (matches <see cref="AuditableEntity.CreatedAtUtc"/>).</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>Whether an operator has acknowledged the finding.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>UTC timestamp of the acknowledgement, when applicable.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// FK to the <see cref="UserProfile"/> who acknowledged the finding. Stored as a
    /// raw long per the internal-fk convention; the wire DTO carries the Sqid-encoded
    /// form.
    /// </summary>
    public long? AcknowledgedByUserId { get; set; }

    /// <summary>
    /// Free-form note accompanying the acknowledgement (3..1000 chars when
    /// set; null while the finding is unacknowledged). Capped at 1000 characters.
    /// </summary>
    public string? AcknowledgementNote { get; set; }
}
