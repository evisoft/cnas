using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC17 phase 2B — Renders a persisted DOCX template by substituting
/// <c>{{placeholder}}</c> markers with values from a runtime dictionary. Used by
/// <see cref="IDocumentGenerationService"/> as a fallback path when no DI-baked
/// <c>Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate</c> matches the
/// requested code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistent vs DI-baked split.</b> The 35 DI-baked <c>IDocxTemplate</c>
/// implementations are strongly-typed renderers — each one knows its own required
/// facts (camelCase keys with mixed value types) and lays out the OpenXML graph
/// programmatically. Persisted templates work the other way around: an operator
/// uploads a hand-authored <c>.docx</c> with <c>{{key}}</c> markers in the body
/// text, and at render time the renderer walks the document and replaces every
/// marker with the matching string from a runtime dictionary. This trade-off
/// favours operational flexibility (no recompile to ship a new template) at the
/// cost of expressive power (no conditionals, no loops, no typed facts).
/// </para>
/// <para>
/// <b>String-only data shape.</b> The data dictionary uses
/// <see cref="string"/> values because the uploaded template author has no
/// compile-time contract to express type expectations. Callers format dates,
/// money, and Sqid-encoded ids into strings BEFORE handing the dictionary to
/// <see cref="RenderAsync"/>. This matches the operator-authoring mental model:
/// "the placeholder is just text".
/// </para>
/// <para>
/// <b>Identifier discipline.</b> Template codes are stable kebab-case strings
/// (e.g. <c>my-custom-template</c>) matched case-insensitively, mirroring the
/// rest of the template stack. They are not Sqid-encoded (CLAUDE.md RULE 3
/// documented exception — code is a stable kebab string, not a sequential
/// surrogate key).
/// </para>
/// </remarks>
public interface IUploadedTemplateRenderer
{
    /// <summary>
    /// True when <paramref name="templateCode"/> maps to a current persistent
    /// template row (matched case-insensitively against the canonical lower-case
    /// form). False when no row exists, the row is superseded
    /// (<c>IsCurrent = false</c>), or the row is soft-deleted
    /// (<c>IsActive = false</c>). Whitespace / null codes return false rather
    /// than throwing.
    /// </summary>
    /// <param name="templateCode">Stable template code (case-insensitive match).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a render via <see cref="RenderAsync"/> would resolve a row.</returns>
    Task<bool> CanRenderAsync(string templateCode, CancellationToken ct = default);

    /// <summary>
    /// Loads the binary from storage, substitutes every <c>{{key}}</c> placeholder
    /// using the supplied <paramref name="data"/> dictionary, and returns the
    /// rendered DOCX bytes. Unknown placeholders (keys not present in
    /// <paramref name="data"/>) are left in place verbatim — they do not throw or
    /// produce a failure.
    /// </summary>
    /// <param name="templateCode">Stable template code (case-insensitive match).</param>
    /// <param name="data">
    /// Placeholder values keyed by placeholder name. Case-sensitive — the
    /// placeholder <c>{{Name}}</c> matches the key <c>Name</c> exactly, not
    /// <c>name</c>. Empty dictionaries are legal (every placeholder is left
    /// verbatim).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the rendered DOCX bytes on
    /// success. Failure paths and their stable error codes:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.NotFound"/> — no persistent template row matches the code (whitespace / null included).</item>
    ///   <item><see cref="ErrorCodes.FileUnavailable"/> — the row exists but the
    ///         backing binary cannot be retrieved from storage (e.g.
    ///         the missing-MinIO sentinel was hit). The underlying storage error
    ///         code is propagated when distinct from FileUnavailable.</item>
    /// </list>
    /// </returns>
    Task<Result<byte[]>> RenderAsync(
        string templateCode,
        IReadOnlyDictionary<string, string> data,
        CancellationToken ct = default);
}
