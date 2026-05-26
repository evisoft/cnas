using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0133 / CF 17.16 — Render-time look-up port that resolves the
/// <see cref="ResolvedTemplateVariant"/> a renderer should use for a given
/// (templateId, requestedLanguage) pair. Implements the documented fall-back rule:
/// when the requested locale has no approved variant, the resolver falls back to
/// the template's <see cref="Cnas.Ps.Core.Domain.DocumentTemplate.DefaultLanguage"/>
/// and increments the <c>cnas.template.render.fallback{from,to}</c> counter so
/// operators can chart missing-translation rates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separation from <see cref="ITemplateVariantService"/>.</b> The variant
/// service owns admin lifecycle (upsert / approve / list); this resolver owns the
/// hot-path render look-up. Splitting the two keeps the cardinality of the
/// resolver's surface tiny (one method) so it can be substituted in renderer
/// tests without dragging in the audit / clock dependencies.
/// </para>
/// <para>
/// <b>Approved-only.</b> The resolver treats unapproved rows as if they did not
/// exist. A draft EN translation never bleeds into a citizen-facing render — the
/// renderer falls back to RO until an admin signs off.
/// </para>
/// </remarks>
public interface ITemplateVariantResolver
{
    /// <summary>
    /// Resolves the variant a renderer should use for one (template, language)
    /// pair. When <paramref name="requestedLanguage"/> is <see langword="null"/>,
    /// the resolver uses the template's default language directly without
    /// incrementing the fall-back counter (callers that don't request a specific
    /// locale are not "falling back" — they got exactly what they asked for).
    /// </summary>
    /// <param name="templateId">Internal id of the parent template.</param>
    /// <param name="requestedLanguage">
    /// Desired locale code (canonical lower-case), or <see langword="null"/> to
    /// route directly to the template's default language.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the resolved variant payload
    /// (always populated when the underlying template exists); failure with
    /// <see cref="ErrorCodes.NotFound"/> when the template itself does not resolve
    /// or when neither the requested nor the default language has a usable variant.
    /// </returns>
    Task<Result<ResolvedTemplateVariant>> ResolveAsync(
        long templateId,
        string? requestedLanguage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R0133 — Resolved variant payload returned by
/// <see cref="ITemplateVariantResolver.ResolveAsync"/>. Carries the language code
/// the renderer should actually use (which may differ from the request when a
/// fall-back occurred) plus the translated subject + body to render.
/// </summary>
/// <param name="TemplateId">Internal id of the parent template.</param>
/// <param name="Language">
/// Locale code the renderer is using — may differ from the request when a
/// fall-back occurred.
/// </param>
/// <param name="SubjectOrTitle">Translated subject/title for the resolved locale.</param>
/// <param name="Body">Translated body for the resolved locale.</param>
/// <param name="FellBack">
/// <see langword="true"/> when the resolver returned the default-language variant
/// because the requested locale had no approved variant.
/// </param>
public sealed record ResolvedTemplateVariant(
    long TemplateId,
    string Language,
    string SubjectOrTitle,
    string Body,
    bool FellBack);
