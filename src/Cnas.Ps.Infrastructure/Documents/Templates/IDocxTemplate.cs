using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Per-template DOCX builder contract. Each Annex 7 named template has a dedicated
/// implementation that knows the exact layout (sections, tables, signature blocks, ...)
/// the business expects. Implementations are stateless and thread-safe; they are
/// registered as singletons and resolved by <see cref="TemplateCode"/> at render time.
/// </summary>
/// <remarks>
/// <para>
/// The unknown-template path is preserved by <c>DocumentGenerationService</c> — when no
/// <see cref="IDocxTemplate"/> matches the requested code, the service falls back to its
/// generic <c>BuildDocx</c> emitter, which produces a minimal <c>{field}: {value}</c>
/// document. This lets the codebase ship templated documents incrementally without
/// breaking the catch-all path.
/// </para>
/// <para>
/// Facts are passed as <see cref="IReadOnlyDictionary{TKey, TValue}"/> with case-sensitive
/// camelCase keys to match the JSON shape used elsewhere in the system. Missing required
/// facts return <see cref="ErrorCodes.TemplateMissingFacts"/> rather than throwing — this
/// is a business-validation failure (CLAUDE.md §2.1 Result Pattern).
/// </para>
/// <para>
/// All timestamps in facts MUST be UTC (CLAUDE.md UTC Everywhere). External identifiers
/// passed in facts (e.g. dossier ids) MUST be pre-encoded Sqid strings — templates never
/// decode/re-encode (CLAUDE.md RULE 3).
/// </para>
/// </remarks>
public interface IDocxTemplate
{
    /// <summary>
    /// Stable template identifier — matched case-insensitively against the requested code
    /// by <c>DocumentGenerationService</c>. Each implementation exposes its own
    /// <c>public const string Code</c> field as the canonical wire-format identifier.
    /// </summary>
    string TemplateCode { get; }

    /// <summary>
    /// Renders the template against the supplied facts. The dictionary keys are
    /// case-sensitive camelCase strings; each template documents its required keys on
    /// its concrete type. Returns <see cref="ErrorCodes.TemplateMissingFacts"/> on
    /// missing-required-fact, or a successful <see cref="Result{T}"/> wrapping the
    /// rendered DOCX bytes.
    /// </summary>
    /// <param name="facts">Render facts keyed by camelCase field name.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the DOCX bytes (ZIP envelope), or
    /// <see cref="Result{T}.Failure(string, string)"/> with
    /// <see cref="ErrorCodes.TemplateMissingFacts"/> when required facts are absent.
    /// </returns>
    Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts);
}
