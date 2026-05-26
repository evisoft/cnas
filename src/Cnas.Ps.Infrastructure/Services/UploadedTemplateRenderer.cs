using System.Text;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC17 phase 2B — Renders persisted operator-uploaded DOCX templates by walking the
/// document's body, substituting every <c>{{key}}</c> placeholder with the matching
/// value from the supplied runtime dictionary. Used by
/// <c>DocumentGenerationService</c> as a fallback path when no DI-baked
/// <c>Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate</c> matches the
/// requested code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multi-run substitution trade-off.</b> Word splits placeholder text across
/// multiple <c>&lt;w:r&gt;&lt;w:t&gt;</c> elements when formatting changes mid-token —
/// for example, bolding only the <c>{{</c> opener produces a paragraph whose runs
/// are <c>"{{"</c>, <c>"name"</c>, <c>"}}"</c>. A naive per-<c>&lt;w:t&gt;</c> walk
/// would miss every multi-run placeholder. To handle this correctly the renderer
/// collapses each paragraph's runs into a single string, performs the
/// substitution on the concatenated text, and — when the result changed — REPLACES
/// the entire paragraph's run sequence with a single
/// <c>&lt;w:r&gt;&lt;w:t xml:space="preserve"&gt;{result}&lt;/w:t&gt;&lt;/w:r&gt;</c>.
/// This loses run-level formatting WITHIN the paragraph (a bold word inside an
/// otherwise normal paragraph becomes plain), but preserves paragraph-level
/// formatting (the paragraph style, alignment, spacing — everything outside the
/// run remains untouched). Operators authoring uploaded templates should use
/// uniform formatting within paragraphs that contain placeholders. Paragraphs
/// without placeholders are left bit-for-bit identical so existing formatting
/// survives the round trip.
/// </para>
/// <para>
/// <b>Scope and known limitations.</b>
/// <list type="bullet">
///   <item><b>Tables:</b> cell text uses the same <c>&lt;w:p&gt;</c> shape, so the
///         paragraph-level pass handles table-cell placeholders correctly.</item>
///   <item><b>Images:</b> out of scope. Placeholders inside image alt text or
///         captions ride along whenever they happen to surface as <c>&lt;w:t&gt;</c>
///         descendants of a body paragraph; nothing else about the image is
///         touched.</item>
///   <item><b>Headers and footers:</b> phase 2B scope is the main
///         <see cref="MainDocumentPart.Document"/>'s <see cref="Body"/>. Headers and
///         footers (<c>HeaderPart</c>, <c>FooterPart</c>) stay verbatim — known
///         limitation that the documentation surfaces to operators.</item>
///   <item><b>Nested placeholders</b> (e.g. <c>{{outer{{inner}}}}</c>): not
///         supported. The regex is single-pass and non-greedy; the outer pair is
///         not re-evaluated after the inner substitution. Authoring guidance is
///         to keep placeholders flat.</item>
///   <item><b>Whitespace inside the marker:</b> only the literal form
///         <c>{{key}}</c> is recognised. Variants such as <c>{{ key }}</c> or
///         <c>{{key }}</c> are intentionally not matched so the substitution
///         alphabet stays explicit.</item>
/// </list>
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — depends on the per-request
/// <see cref="ICnasDbContext"/>. The
/// <see cref="IFileStorage"/> collaborator is a singleton; the
/// <see cref="ILogger{TCategoryName}"/> instance is whatever DI provides.
/// </para>
/// </remarks>
public sealed partial class UploadedTemplateRenderer : IUploadedTemplateRenderer
{
    /// <summary>
    /// MinIO bucket name used to store template binaries — mirrors
    /// <c>TemplateAdminService.TemplatesBucket</c>. Centralised as a constant so the
    /// renderer and the admin service read the same value; if the bucket name ever
    /// changes the rename happens in both places via the constant.
    /// </summary>
    private const string TemplatesBucket = "cnas-templates";

    /// <summary>
    /// Non-greedy <c>{{key}}</c> placeholder regex. The capture group permits the
    /// kebab-case identifier alphabet operators are expected to use (letters,
    /// digits, hyphen, underscore). The match is non-greedy so adjacent
    /// placeholders on the same line each match individually rather than being
    /// concatenated into one giant capture. Anchored with literal braces — no
    /// whitespace tolerance inside the marker (see class XML doc).
    /// </summary>
    [GeneratedRegex(@"\{\{([a-zA-Z0-9_-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Per-request EF Core context abstraction.</summary>
    private readonly ICnasDbContext _db;

    /// <summary>Object-storage adapter — MinIO in production, in-memory in tests.</summary>
    private readonly IFileStorage _storage;

    /// <summary>Structured logger (CLAUDE.md §6.1).</summary>
    private readonly ILogger<UploadedTemplateRenderer> _logger;

    /// <summary>
    /// Constructs the renderer. All collaborators are required; null arguments throw
    /// <see cref="ArgumentNullException"/>.
    /// </summary>
    /// <param name="db">Per-request EF Core context backing the
    /// <c>DocumentTemplates</c> lookup.</param>
    /// <param name="storage">Object-storage adapter used to fetch the binary blob.</param>
    /// <param name="logger">Structured logger.</param>
    public UploadedTemplateRenderer(
        ICnasDbContext db,
        IFileStorage storage,
        ILogger<UploadedTemplateRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _storage = storage;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> CanRenderAsync(string templateCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateCode))
        {
            return false;
        }

        // Canonicalise (trim + lower) so the lookup matches the storage convention
        // documented on TemplateAdminService.UploadAsync — the row's Code is always
        // lower-case and trimmed.
        var canonical = templateCode.Trim().ToLowerInvariant();
        return await _db.DocumentTemplates
            .AnyAsync(t => t.Code == canonical && t.IsCurrent && t.IsActive, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> RenderAsync(
        string templateCode,
        IReadOnlyDictionary<string, string> data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(templateCode))
        {
            return Result<byte[]>.Failure(
                ErrorCodes.NotFound,
                "Template code must not be null or whitespace.");
        }

        var canonical = templateCode.Trim().ToLowerInvariant();

        // 1. Resolve the current persistent row.
        var row = await _db.DocumentTemplates
            .Where(t => t.Code == canonical && t.IsCurrent && t.IsActive)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<byte[]>.Failure(
                ErrorCodes.NotFound,
                $"No persistent template registered with code '{templateCode}'.");
        }

        // 2. Pull the binary from storage. The sentinel
        //    (Cnas.Ps.Infrastructure.Storage.MissingMinioFileStorage) throws
        //    InvalidOperationException at the IFileStorage call site when MinIO is
        //    unconfigured — translate to a clean Result.Failure so the dispatcher
        //    surface can branch on it without try/catch.
        Result<Stream> blob;
        try
        {
            blob = await _storage.GetAsync(TemplatesBucket, row.StorageObjectKey, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Storage adapter threw while fetching template {TemplateCode} (object {ObjectKey}); treating as FileUnavailable.",
                canonical,
                row.StorageObjectKey);
            return Result<byte[]>.Failure(
                ErrorCodes.FileUnavailable,
                "Template storage adapter is unavailable.");
        }

        if (blob.IsFailure)
        {
            return Result<byte[]>.Failure(blob.ErrorCode!, blob.ErrorMessage!);
        }

        // 3. Buffer the bytes — OpenXML needs a seekable stream and the storage
        //    contract does not promise one.
        using var input = blob.Value;
        using var workingStream = new MemoryStream();
        await input.CopyToAsync(workingStream, ct).ConfigureAwait(false);

        // 4. Open the package in-place for editing, walk every paragraph, and
        //    substitute placeholders. We re-use the same MemoryStream so we can
        //    ToArray() it once the package's Dispose flushes the central directory.
        workingStream.Position = 0;
        using (var package = WordprocessingDocument.Open(workingStream, isEditable: true))
        {
            var body = package.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                _logger.LogWarning(
                    "Template {TemplateCode} has no MainDocumentPart.Document.Body; returning bytes verbatim.",
                    canonical);
            }
            else
            {
                SubstitutePlaceholders(body, data);
                package.MainDocumentPart!.Document.Save();
            }
        }

        return Result<byte[]>.Success(workingStream.ToArray());
    }

    /// <summary>
    /// Walks every <see cref="Paragraph"/> descendant of <paramref name="root"/> —
    /// which includes paragraphs nested inside <see cref="Table"/> cells — and
    /// substitutes <c>{{key}}</c> placeholders on a paragraph-by-paragraph basis.
    /// See the class-level XML doc for the multi-run trade-off rationale.
    /// </summary>
    /// <param name="root">The OpenXML root to scan — typically <see cref="Body"/>.</param>
    /// <param name="data">Placeholder values keyed by placeholder name.</param>
    private static void SubstitutePlaceholders(OpenXmlElement root, IReadOnlyDictionary<string, string> data)
    {
        // ToList() the snapshot so we can mutate the tree (RemoveAllChildren on a
        // paragraph) without invalidating the enumeration.
        var paragraphs = root.Descendants<Paragraph>().ToList();
        foreach (var paragraph in paragraphs)
        {
            // Collect the text runs at the paragraph level; ignore runs inside nested
            // paragraphs (e.g. text-boxes) because they will be visited as their own
            // Paragraph entries above.
            var texts = paragraph.Descendants<Text>().ToList();
            if (texts.Count == 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            foreach (var t in texts)
            {
                sb.Append(t.Text);
            }
            var original = sb.ToString();

            // Cheap fast-path: paragraphs without any placeholder marker stay
            // untouched, preserving run-level formatting bit-for-bit.
            if (!original.Contains("{{", StringComparison.Ordinal))
            {
                continue;
            }

            var replaced = PlaceholderRegex().Replace(original, m =>
            {
                var key = m.Groups[1].Value;
                return data.TryGetValue(key, out var value) ? value : m.Value;
            });

            if (string.Equals(replaced, original, StringComparison.Ordinal))
            {
                // The marker existed but no placeholder resolved (every key was
                // absent from the data dict). Leave the paragraph alone so the
                // verbatim markers survive in their original runs.
                continue;
            }

            ReplaceParagraphTextWithSingleRun(paragraph, replaced);
        }
    }

    /// <summary>
    /// Replaces the entire run sequence of <paramref name="paragraph"/> with a single
    /// <see cref="Run"/> carrying the substituted text. Paragraph-level properties
    /// (<see cref="ParagraphProperties"/>) are preserved — only run-level structure
    /// is rewritten. This is the trade-off documented at class level: paragraph
    /// formatting (alignment, style, spacing) survives; in-paragraph mixed
    /// formatting (bold word inside otherwise normal text) does not.
    /// </summary>
    /// <param name="paragraph">Paragraph to rewrite.</param>
    /// <param name="text">New text content for the paragraph's single run.</param>
    private static void ReplaceParagraphTextWithSingleRun(Paragraph paragraph, string text)
    {
        // Preserve the ParagraphProperties (if any) — it carries the style id,
        // alignment, spacing, indent, bullet list reference, etc. Anything that is
        // NOT a Run goes back into the paragraph in its original order; the runs
        // collapse into the single replacement run inserted after the properties.
        var paragraphProperties = paragraph.GetFirstChild<ParagraphProperties>()?.CloneNode(true);

        paragraph.RemoveAllChildren();

        if (paragraphProperties is not null)
        {
            paragraph.AppendChild(paragraphProperties);
        }

        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(run);
    }
}
