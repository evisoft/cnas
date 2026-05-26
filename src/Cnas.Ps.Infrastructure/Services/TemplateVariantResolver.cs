using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0133 / TOR CF 17.16 — Concrete <see cref="ITemplateVariantResolver"/>. Implements
/// the fall-back rule:
/// <list type="number">
///   <item>If the caller did not request a specific locale (<see langword="null"/>),
///         resolve the template's default-language variant directly — no fall-back
///         counter is incremented.</item>
///   <item>If the requested locale has an approved variant, return it.</item>
///   <item>Otherwise return the default-language variant, mark <c>FellBack=true</c>,
///         and increment <c>cnas.template.render.fallback{from,to}</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Approved-only.</b> Unapproved rows are invisible to the resolver — the SQL
/// predicate requires <c>IsApproved=true</c>. A draft EN translation does not bleed
/// into a citizen-facing render until an admin flips the row through
/// <see cref="ITemplateVariantService.ApproveAsync"/>.
/// </para>
/// <para>
/// <b>Metric cardinality.</b> The fall-back counter tags carry the canonical
/// lower-case language codes (<c>ro</c>, <c>en</c>, <c>ru</c>). Cardinality is
/// bounded by <c>TemplateLanguages.All.Count</c> ^ 2 — at most 9 tag combinations.
/// </para>
/// </remarks>
public sealed class TemplateVariantResolver : ITemplateVariantResolver
{
    private readonly ICnasDbContext _db;

    /// <summary>
    /// Constructs the resolver.
    /// </summary>
    /// <param name="db">EF Core context (scoped).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="db"/> is null.</exception>
    public TemplateVariantResolver(ICnasDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Result<ResolvedTemplateVariant>> ResolveAsync(
        long templateId,
        string? requestedLanguage,
        CancellationToken cancellationToken = default)
    {
        var template = await _db.DocumentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return Result<ResolvedTemplateVariant>.Failure(
                ErrorCodes.NotFound,
                $"No template found with id {templateId}.");
        }

        // Step 1: try the requested locale, but only if approved. We accept a null
        // requested language as "use default outright" — that's not a fall-back.
        if (!string.IsNullOrWhiteSpace(requestedLanguage)
            && !string.Equals(requestedLanguage, template.DefaultLanguage, StringComparison.Ordinal))
        {
            var requested = await _db.TemplateVariants
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    v => v.TemplateId == templateId
                        && v.Language == requestedLanguage
                        && v.IsApproved
                        && v.IsActive,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requested is not null)
            {
                return Result<ResolvedTemplateVariant>.Success(new ResolvedTemplateVariant(
                    TemplateId: template.Id,
                    Language: requested.Language,
                    SubjectOrTitle: requested.SubjectOrTitle,
                    Body: requested.Body,
                    FellBack: false));
            }
            // Requested locale missing/unapproved → fall-back. Record the metric BEFORE
            // returning so a caller that ignores the result still sees the signal.
            CnasMeter.TemplateRenderFallback.Add(
                1,
                new KeyValuePair<string, object?>("from", requestedLanguage),
                new KeyValuePair<string, object?>("to", template.DefaultLanguage));
        }

        // Step 2: default-language variant. Same approval gate — an unapproved
        // default-language row is a programmer error (the seeder back-fills RO
        // approved), but we still treat it as missing rather than crashing.
        var fallback = await _db.TemplateVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.TemplateId == templateId
                    && v.Language == template.DefaultLanguage
                    && v.IsApproved
                    && v.IsActive,
                cancellationToken)
            .ConfigureAwait(false);
        if (fallback is null)
        {
            return Result<ResolvedTemplateVariant>.Failure(
                ErrorCodes.NotFound,
                $"Template {template.Code} has no approved default-language variant.");
        }

        // FellBack only when the caller asked for a SPECIFIC, NON-DEFAULT locale.
        var fellBack = !string.IsNullOrWhiteSpace(requestedLanguage)
            && !string.Equals(requestedLanguage, template.DefaultLanguage, StringComparison.Ordinal);
        return Result<ResolvedTemplateVariant>.Success(new ResolvedTemplateVariant(
            TemplateId: template.Id,
            Language: fallback.Language,
            SubjectOrTitle: fallback.SubjectOrTitle,
            Body: fallback.Body,
            FellBack: fellBack));
    }
}
