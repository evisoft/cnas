namespace Cnas.Ps.Contracts;

/// <summary>
/// R0133 — Stable lower-case ISO 639-1 language codes accepted by the
/// template-variant upsert / lookup surfaces. Hard-coded to the three locales the
/// product supports (Romanian, English, Russian) so a typo at the API boundary fails
/// fast rather than silently creating a "de" variant nobody can render.
/// </summary>
/// <remarks>
/// <para>
/// The codes are exposed as compile-time constants (not an enum) so JSON payloads
/// carry stable lower-case strings rather than enum-style identifiers — the wire
/// format must match the column-level <c>varchar(8)</c> storage in
/// <c>Cnas.Ps.Core.Domain.TemplateVariant.Language</c>. The
/// <see cref="All"/> set is the FluentValidation source of truth.
/// </para>
/// <para>
/// Adding a fourth locale (Ukrainian etc.) is an additive change — append the code to
/// <see cref="All"/> and document the migration's back-fill expectations. Removing a
/// code is a breaking change that requires deleting every variant row whose
/// language matches the removed code.
/// </para>
/// </remarks>
public static class TemplateLanguages
{
    /// <summary>Romanian — the default authoring locale for the 35 baked-in templates.</summary>
    public const string Ro = "ro";

    /// <summary>English translation locale.</summary>
    public const string En = "en";

    /// <summary>Russian translation locale.</summary>
    public const string Ru = "ru";

    /// <summary>The set of accepted language codes. Order is alphabetical for determinism.</summary>
    public static readonly IReadOnlyCollection<string> All = [En, Ro, Ru];
}

/// <summary>
/// R0133 / CF 17.16 — Input DTO posted to
/// <c>PUT /api/admin/templates/{templateSqid}/variants/{language}</c>. Carries the
/// translated subject, body, and (optionally) a pre-translated DOCX blob for one
/// template-language pair.
/// </summary>
/// <param name="TemplateSqid">
/// Sqid-encoded id of the parent <c>Cnas.Ps.Core.Domain.DocumentTemplate</c>.
/// Decoded by the service layer to the raw long primary key per CLAUDE.md RULE 3.
/// </param>
/// <param name="Language">
/// Lower-case language code (must be one of <see cref="TemplateLanguages.All"/>).
/// </param>
/// <param name="SubjectOrTitle">Required subject/title 1..200 chars.</param>
/// <param name="Body">Translated body 1..100,000 chars.</param>
/// <param name="TranslatorNote">Optional free-text note left by the translator.</param>
/// <param name="DocxBase64">
/// Optional base64-encoded pre-translated DOCX blob. When present, decoded bytes are
/// validated for size (≤ 10 MiB) and DOCX magic bytes (<c>PK\x03\x04</c>) at the
/// boundary; when absent, the renderer falls back to regenerating the docx from
/// <see cref="Body"/> at render time.
/// </param>
public sealed record TemplateVariantUpsertDto(
    string TemplateSqid,
    string Language,
    string SubjectOrTitle,
    string Body,
    string? TranslatorNote = null,
    string? DocxBase64 = null);

/// <summary>
/// R0133 / CF 17.16 — Output DTO returned by the upsert / list / get endpoints. The
/// <see cref="BodyPreview"/> field exists so listing endpoints do not have to ship the
/// full body over the wire — a 240-char prefix is enough for the admin UI's preview
/// column.
/// </summary>
/// <param name="Id">Sqid-encoded id of the variant row.</param>
/// <param name="TemplateSqid">Sqid-encoded parent template id.</param>
/// <param name="Language">Stable lower-case language code.</param>
/// <param name="SubjectOrTitle">Required subject/title.</param>
/// <param name="BodyPreview">First 240 characters of the body (or the full body if shorter).</param>
/// <param name="IsApproved">Approval flag — <c>false</c> until an admin flips it.</param>
/// <param name="TranslatorNote">Optional translator note.</param>
/// <param name="HasDocx">
/// <see langword="true"/> when the row carries a non-null
/// <c>Cnas.Ps.Core.Domain.TemplateVariant.RenderedDocxBytes</c> blob.
/// </param>
public sealed record TemplateVariantOutputDto(
    string Id,
    string TemplateSqid,
    string Language,
    string SubjectOrTitle,
    string BodyPreview,
    bool IsApproved,
    string? TranslatorNote,
    bool HasDocx);

/// <summary>
/// R0134 / CF 17.17 — Summary of an XML or CSV catalog-import run. Returned by
/// <c>ITemplateCatalogPort.ImportXmlAsync</c> / <c>ImportCsvAsync</c> in the success
/// path; on validation-failure paths the import is rolled back and the report is
/// returned inside a failure <c>Result</c> so callers see the warnings + errors.
/// </summary>
/// <param name="Created">Number of variant rows freshly inserted.</param>
/// <param name="Updated">Number of variant rows updated in place.</param>
/// <param name="Skipped">Rows the importer refused to touch (e.g. unknown template code).</param>
/// <param name="Warnings">Non-fatal advisory messages emitted during import.</param>
/// <param name="Errors">
/// Fatal validation errors. When this list is non-empty the import is aborted and no
/// rows are persisted (all-or-nothing semantics).
/// </param>
public sealed record TemplateCatalogImportReportDto(
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
