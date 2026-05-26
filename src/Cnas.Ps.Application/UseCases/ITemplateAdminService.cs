using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC17 — Administrative surface for the DOCX template registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 (batch #94) — read-only catalog over the DI-baked singletons.</b> The 35
/// Annex 7 templates are compiled into the Infrastructure assembly and registered via
/// <c>AddSingleton&lt;IDocxTemplate, ...&gt;()</c> at composition time. The interface
/// projects them through a flat catalog of <see cref="TemplateCatalogEntry"/> records.
/// </para>
/// <para>
/// <b>Phase 2A — persistence + binary upload.</b> Operators can upload new DOCX templates
/// (and new versions of existing ones) via <see cref="UploadAsync"/>. The catalog
/// endpoints (<see cref="ListAsync"/> / <see cref="GetAsync"/>) union BOTH sources:
/// DI-baked singletons AND persistent rows. When a code collides between the two,
/// the persistent row wins (operator-override semantics) — see the service-layer
/// implementation for the precise tiebreak. Phase 2B will unify the rendering pipeline
/// so uploaded templates render through the same dispatch as DI-baked ones.
/// </para>
/// <para>
/// <b>Authorization gate.</b> The HTTP surface
/// (<see cref="Cnas.Ps.Contracts.TemplateCatalogEntry"/> producing
/// <c>TemplatesController</c>) requires the
/// <c>CnasAdmin</c> policy. Template management is catalog administration — not a
/// reporting / data-export action — so the lower-privileged <c>CnasUser</c> / <c>CnasDecider</c>
/// roles must not see the catalog. The 403 path is locked by the Uc17 E2E journey.
/// </para>
/// <para>
/// <b>Identifier discipline.</b> Template codes are stable kebab-case strings
/// (<c>"refuz-aplicare"</c>) — they are <em>not</em> Sqid-encoded surrogate keys.
/// See the XML doc on <see cref="TemplateCatalogEntry"/> for the documented Sqid (RULE 3)
/// exception rationale.
/// </para>
/// </remarks>
public interface ITemplateAdminService
{
    /// <summary>
    /// Lists every template code currently registered in DI OR persisted in the
    /// <c>DocumentTemplates</c> table (current versions only), projected to a
    /// <see cref="TemplateCatalogEntry"/>. The returned list is sorted alphabetically by
    /// <see cref="TemplateCatalogEntry.Code"/> so the picker UI does not have to re-sort
    /// client-side and so the response is deterministic across runs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the catalog. The list is empty (never
    /// <see langword="null"/>) when no templates are registered — the empty path is a
    /// legitimate state during early bring-up and must not surface as a failure.
    /// </returns>
    Task<Result<IReadOnlyList<TemplateCatalogEntry>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the catalog entry for a single template identified by its stable
    /// <paramref name="templateCode"/>. Match is case-insensitive (mirrors the renderer
    /// dispatch in <c>DocumentGenerationService</c>); the returned entry echoes the
    /// canonical lower-case form. When the code resolves in both registries the
    /// persistent row wins.
    /// </summary>
    /// <param name="templateCode">Stable template code (e.g. <c>refuz-aplicare</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the matched entry, or
    /// <see cref="Result{T}.Failure(string, string)"/> with
    /// <see cref="ErrorCodes.NotFound"/> when no template matches (including
    /// <see langword="null"/> / whitespace input — the service is tolerant of malformed
    /// keys rather than throwing).
    /// </returns>
    Task<Result<TemplateCatalogEntry>> GetAsync(string templateCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a new persistent template OR a new version of an existing one. Computes
    /// the SHA-256 of the stream while copying it to MinIO; on success inserts a new
    /// <c>DocumentTemplate</c> row with <c>Version = max+1</c> and flips
    /// <c>IsCurrent</c> on the previous current row (if any) to <c>false</c>. Returns
    /// the newly-created catalog entry so the caller can show it immediately without a
    /// follow-up <see cref="GetAsync"/>.
    /// </summary>
    /// <param name="code">
    /// Stable kebab-case template code (e.g. <c>my-custom-template</c>). Validated against
    /// the regex <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> and a 96-char maximum length. On
    /// success the row is persisted with this canonical (lower-case, trimmed) form.
    /// </param>
    /// <param name="name">
    /// Human-readable display name (≤ 256 chars). Surfaces in the catalog listing and
    /// the admin UI's template-picker drop-down.
    /// </param>
    /// <param name="description">
    /// Optional free-text purpose / usage note. <see langword="null"/> when the operator
    /// omits it; persisted as <c>NULL</c> in the database.
    /// </param>
    /// <param name="content">
    /// Open, readable stream positioned at byte 0 carrying the DOCX binary. The service
    /// consumes the stream end-to-end and computes the SHA-256 as it goes. Caller
    /// disposes the stream after the call returns.
    /// </param>
    /// <param name="contentType">
    /// Declared MIME type. Must be exactly
    /// <c>application/vnd.openxmlformats-officedocument.wordprocessingml.document</c> —
    /// any other value returns <see cref="ErrorCodes.FileTypeMismatch"/>. The first
    /// four bytes of the stream are also sniffed against the ZIP magic (<c>50 4B 03 04</c>)
    /// to enforce CLAUDE.md §5.1.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the new catalog entry on success.
    /// Failure paths and their stable error codes:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.ValidationFailed"/> — code/name violates length or kebab-case rules.</item>
    ///   <item><see cref="ErrorCodes.FileTypeMismatch"/> — wrong MIME type or magic-byte mismatch.</item>
    ///   <item><see cref="ErrorCodes.FileTooLarge"/> — content exceeds the <c>MaxTemplateSize</c> cap (5 MiB).</item>
    /// </list>
    /// </returns>
    Task<Result<TemplateCatalogEntry>> UploadAsync(
        string code,
        string name,
        string? description,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads the binary for a persistent template's current version. Reads the row
    /// keyed by <paramref name="code"/> (case-insensitive), fetches the blob from MinIO,
    /// and wraps it in a <see cref="TemplateDownloadStream"/>. Returns
    /// <see cref="ErrorCodes.NotFound"/> when no persistent row exists for the code
    /// (including the "DI-baked-only" case — DI templates have no stored blob and
    /// therefore cannot be downloaded; phase 2B may revisit this once the renderer
    /// pipeline is unified).
    /// </summary>
    /// <param name="code">Stable template code (case-insensitive match).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the download stream, or
    /// <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.FileUnavailable"/> on
    /// failure (the latter when the row exists but MinIO cannot serve the blob).
    /// </returns>
    Task<Result<TemplateDownloadStream>> DownloadAsync(string code, CancellationToken ct = default);
}
