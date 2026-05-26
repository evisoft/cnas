namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0133 / TOR CF 17.16 / UI 003 — per-language variant of a
/// <see cref="DocumentTemplate"/>. Each row carries the translated subject + body for one
/// language code (<c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>); at most one row exists per
/// <c>(TemplateId, Language)</c> pair. The renderer dispatch looks up the variant by
/// requested locale and falls back to <see cref="DocumentTemplate.DefaultLanguage"/> when
/// the requested locale either does not exist or has not been approved by an admin.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval workflow.</b> Translated variants start with <see cref="IsApproved"/> =
/// <c>false</c> so a half-translated draft cannot reach the citizen. An admin flips the
/// row to approved via <c>ITemplateVariantService.ApproveAsync</c> after a manual
/// review; the corresponding <c>UnapproveAsync</c> reverses the flag. The renderer
/// fall-back path treats unapproved variants as if they did not exist.
/// </para>
/// <para>
/// <b>Optional rendered DOCX bytes.</b> When the original RO upload was a
/// <c>.docx</c> file the EN/RU variants may either keep a pre-translated docx blob in
/// <see cref="RenderedDocxBytes"/> OR rely on the renderer to materialise one at render
/// time from <see cref="Body"/>. The renderer pipeline is responsible for picking the
/// strategy — the entity is agnostic.
/// </para>
/// <para>
/// <b>Sqid discipline.</b> <see cref="AuditableEntity.Id"/> is exposed externally as a
/// Sqid-encoded string through the DTO layer per CLAUDE.md RULE 3 — hence the
/// <see cref="IExternalId"/> marker. The internal FK <see cref="TemplateId"/> is a raw
/// <c>long</c> as is normal for internal joins.
/// </para>
/// </remarks>
public sealed class TemplateVariant : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="DocumentTemplate"/> row.</summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Stable lower-case ISO-639-1 language code identifying the variant locale. Limited
    /// to <c>"ro"</c>, <c>"en"</c>, or <c>"ru"</c> by the FluentValidation guard in the
    /// service layer. Persisted as-is — no canonicalisation in the database, so the
    /// service layer must always pass the canonical form.
    /// </summary>
    public required string Language { get; set; }

    /// <summary>
    /// Required subject (for email-style templates) or title (for document templates).
    /// 1..200 characters. Surfaced in the catalog listing and in the rendered output's
    /// header so a translator may set the per-locale display name.
    /// </summary>
    public required string SubjectOrTitle { get; set; }

    /// <summary>
    /// Template content for this language — Markdown or HTML matching the parent
    /// template's authoring format. The renderer substitutes <c>{{placeholder}}</c>
    /// markers in the body at render time. ≤ 100,000 characters.
    /// </summary>
    public required string Body { get; set; }

    /// <summary>
    /// Optional translated DOCX binary. When non-null, the renderer prefers this blob
    /// over the fallback "regenerate from <see cref="Body"/>" path. Subject to the same
    /// 10-MiB cap enforced by the upsert validator.
    /// </summary>
    public byte[]? RenderedDocxBytes { get; set; }

    /// <summary>
    /// Suggested filename for <see cref="RenderedDocxBytes"/>; populated only when the
    /// blob is non-null. Used by the download endpoint as the
    /// <c>Content-Disposition</c> filename suggestion.
    /// </summary>
    public string? DocxFileName { get; set; }

    /// <summary>
    /// Approval flag. Defaults to <c>false</c>; an admin flips this to <c>true</c> via
    /// <c>ITemplateVariantService.ApproveAsync</c> after reviewing the translation. The
    /// renderer fall-back path treats unapproved variants as missing.
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Optional free-text note left by the translator (translation memory references,
    /// disambiguation choices, etc.). Surfaced to admins reviewing the variant; never
    /// rendered in the document itself.
    /// </summary>
    public string? TranslatorNote { get; set; }
}
