using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0133 / TOR CF 17.16 / UI 003 — administrative surface for the per-language
/// <see cref="Cnas.Ps.Core.Domain.TemplateVariant"/> rows that hang off each
/// <see cref="Cnas.Ps.Core.Domain.DocumentTemplate"/>. Upsert is idempotent on
/// <c>(template, language)</c>; approve/unapprove flips the row's
/// <see cref="Cnas.Ps.Core.Domain.TemplateVariant.IsApproved"/> flag and emits a
/// Critical audit event so the change is traceable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval gate.</b> Translated variants land with <c>IsApproved=false</c> so a
/// half-translated draft can never reach the citizen. The renderer's fall-back
/// resolution treats unapproved variants as if they did not exist — even an
/// existing-but-unapproved row routes through the template's default language. An
/// admin must call <see cref="ApproveAsync"/> after a manual review.
/// </para>
/// <para>
/// <b>Audit emission.</b> <see cref="ApproveAsync"/> and <see cref="UnapproveAsync"/>
/// emit the Critical audit codes <c>TEMPLATE.VARIANT.APPROVED</c> and
/// <c>TEMPLATE.VARIANT.UNAPPROVED</c> respectively, with a JSON detail body containing
/// the parent template's <c>Code</c> and the variant's <c>Language</c>. Upsert is
/// considered routine and does NOT emit a per-call audit row; bulk imports via
/// <see cref="ITemplateCatalogPort"/> emit the catalog-level summary audit instead.
/// </para>
/// </remarks>
public interface ITemplateVariantService
{
    /// <summary>
    /// Creates or replaces the variant identified by <c>(TemplateSqid, Language)</c>.
    /// The (template, language) natural key means an upsert is the only sensible
    /// shape — separate create and update endpoints would force the caller to
    /// probe-then-write, which is racy in the presence of concurrent admins.
    /// </summary>
    /// <param name="dto">Upsert input carrying the parent template Sqid, language, and translated payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the persisted row's output DTO.
    /// Failure codes: <see cref="ErrorCodes.ValidationFailed"/> (validator),
    /// <see cref="ErrorCodes.InvalidSqid"/> (template sqid),
    /// <see cref="ErrorCodes.NotFound"/> (template id does not resolve),
    /// <see cref="ErrorCodes.FileTypeMismatch"/> (DocxBase64 magic-byte mismatch),
    /// <see cref="ErrorCodes.FileTooLarge"/> (DocxBase64 over 10 MiB).
    /// </returns>
    Task<Result<TemplateVariantOutputDto>> UpsertAsync(
        TemplateVariantUpsertDto dto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips <see cref="Cnas.Ps.Core.Domain.TemplateVariant.IsApproved"/> to
    /// <c>true</c> and emits a Critical audit row <c>TEMPLATE.VARIANT.APPROVED</c>.
    /// Idempotent — re-approving an already-approved row succeeds without writing
    /// a second audit event.
    /// </summary>
    /// <param name="variantId">Internal id of the variant (already decoded from the Sqid by the caller).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the row is approved (idempotent);
    /// <see cref="ErrorCodes.NotFound"/> when no variant matches the id.
    /// </returns>
    Task<Result> ApproveAsync(long variantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips <see cref="Cnas.Ps.Core.Domain.TemplateVariant.IsApproved"/> back to
    /// <c>false</c> and emits a Critical audit row <c>TEMPLATE.VARIANT.UNAPPROVED</c>.
    /// Idempotent — un-approving an already-unapproved row succeeds without writing
    /// a second audit event.
    /// </summary>
    /// <param name="variantId">Internal id of the variant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the row is unapproved (idempotent);
    /// <see cref="ErrorCodes.NotFound"/> when no variant matches the id.
    /// </returns>
    Task<Result> UnapproveAsync(long variantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the variant matching <c>(templateId, language)</c>, or
    /// <see langword="null"/> when no such row exists. Used by the renderer
    /// fall-back resolver — a null return signals "use the template's default
    /// language".
    /// </summary>
    /// <param name="templateId">Internal id of the parent template.</param>
    /// <param name="language">Canonical lower-case language code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The variant's output DTO, or <see langword="null"/> if no match.</returns>
    Task<TemplateVariantOutputDto?> GetAsync(
        long templateId,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every variant attached to a parent template, sorted alphabetically by
    /// language code so the admin UI's column order is deterministic across runs.
    /// </summary>
    /// <param name="templateId">Internal id of the parent template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of output DTOs (may be empty; never <see langword="null"/>).</returns>
    Task<IReadOnlyList<TemplateVariantOutputDto>> ListAsync(
        long templateId,
        CancellationToken cancellationToken = default);
}
