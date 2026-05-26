using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC17 — Read + write implementation of <see cref="ITemplateAdminService"/>. Unions the
/// DI-injected <see cref="IEnumerable{T}"/> of <see cref="IDocxTemplate"/> singletons
/// (phase 1) with the persistent <c>DocumentTemplates</c> table + MinIO blobs (phase 2A)
/// behind a single <see cref="TemplateCatalogEntry"/>-shaped contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>DI-vs-persistent collision rule.</b> Phase 2A introduces the possibility that a
/// code may exist in both registries (e.g. an operator uploads a custom
/// <c>refuz-aplicare</c> that overrides the DI-baked one). The service resolves the
/// collision in favour of the PERSISTENT row — operator-override semantics: an
/// administrator who took the trouble to upload a custom version has clearly intended
/// it to supersede the baked-in default. The DI-baked row is suppressed from the
/// catalog in that case so the UI does not display two rows for the same code (which
/// would confuse "which one is live?"). The chosen Source string on the surviving
/// row is <c>"Persistent"</c>, signalling to the front-end that the row is operator-
/// managed and can be re-uploaded / downloaded.
/// </para>
/// <para>
/// <b>Lifetime.</b> Phase 2A widens the service to scoped — it now depends on the
/// per-request <see cref="ICnasDbContext"/>. The injected
/// <see cref="IEnumerable{T}"/> of <see cref="IDocxTemplate"/> is still a singleton set
/// (each implementation is a stateless singleton), but the union with the database
/// must happen per-scope.
/// </para>
/// <para>
/// <b>Upload validation pipeline.</b> Every <see cref="UploadAsync"/> walks the same
/// gates in order: (1) <c>code</c> + <c>name</c> shape checks, (2) MIME type exact-match
/// to the DOCX MIME, (3) magic-byte sniff on the first four bytes (<c>50 4B 03 04</c> —
/// ZIP/DOCX) per CLAUDE.md §5.1, (4) size cap enforcement during the SHA-256-computing
/// copy. Failing any gate returns the matching stable <see cref="ErrorCodes"/> value
/// without writing to storage or the database.
/// </para>
/// </remarks>
public sealed partial class TemplateAdminService : ITemplateAdminService
{
    /// <summary>
    /// Maximum accepted upload size in bytes — 5 MiB. Materially smaller than the
    /// generic file-storage cap (<see cref="MinioOptions.MaxFileSizeBytes"/>, 25 MiB)
    /// because DOCX templates are structurally tiny (XML + small embedded assets); a 5
    /// MiB cap comfortably covers realistic Annex 7 sizes while protecting MinIO from
    /// abuse. Centralised as a constant so the validator and the test assertions read
    /// the same value.
    /// </summary>
    public const long MaxTemplateSize = 5L * 1024 * 1024;

    /// <summary>
    /// Required MIME type for uploaded DOCX templates. Matches the
    /// OpenXML wordprocessingml.document MIME exactly — alternative DOCX containers
    /// (e.g. <c>.docm</c> macros) are intentionally rejected in phase 2A so that we
    /// never persist a binary that could carry executable VBA.
    /// </summary>
    public const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// MinIO bucket name used to store template binaries. Distinct from the citizen-
    /// upload and generated-document buckets so retention, lifecycle, and access
    /// policies can be tuned independently. Composed at startup from
    /// <see cref="MinioOptions.GeneratedDocumentsBucket"/> with a deterministic
    /// suffix; centralised here so a future config flip is one constant away.
    /// </summary>
    public const string TemplatesBucket = "cnas-templates";

    /// <summary>
    /// ZIP / DOCX magic-byte signature. The DOCX container is a ZIP file, so every
    /// well-formed DOCX starts with the four bytes <c>0x50 0x4B 0x03 0x04</c>
    /// ("PK"). The magic-byte sniff is the CLAUDE.md §5.1 defence against
    /// extension-only validation — even if the operator forces a non-DOCX file through
    /// the MIME check, the magic bytes will not match and the upload is rejected.
    /// </summary>
    private static readonly byte[] DocxMagicBytes = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Kebab-case validator for template codes. Lower-case letters, digits, and hyphens
    /// only, with no leading / trailing / consecutive hyphens. Compiled because it runs
    /// on every upload.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShapeRegex();

    /// <summary>
    /// Frozen snapshot of the injected DI-baked templates. We materialise once at
    /// construction so repeated <see cref="ListAsync"/> / <see cref="GetAsync"/> calls do
    /// not re-enumerate the underlying DI iterator. The list is immutable for the
    /// service's lifetime — fresh template instances would require an app restart.
    /// </summary>
    private readonly IReadOnlyList<IDocxTemplate> _templates;

    /// <summary>EF Core context abstraction. Per-request scoped.</summary>
    private readonly ICnasDbContext _db;

    /// <summary>Object storage adapter (MinIO in production, in-memory in E2E tests).</summary>
    private readonly IFileStorage _storage;

    /// <summary>UTC clock — never <see cref="DateTime.UtcNow"/> directly (CLAUDE.md UTC Everywhere).</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>
    /// Constructs the service. DI resolves every registered <see cref="IDocxTemplate"/>
    /// implementation into the <paramref name="templates"/> parameter — the same shape
    /// <c>DocumentGenerationService</c> uses to build its render dispatcher — and the
    /// usual scoped collaborators handle the persistent half of the catalog.
    /// </summary>
    /// <param name="templates">
    /// All <see cref="IDocxTemplate"/> singletons registered in DI. May be empty
    /// (the "no DI templates yet" path), but must not be <see langword="null"/>.
    /// </param>
    /// <param name="db">Per-request EF Core context.</param>
    /// <param name="storage">Object storage adapter for the binary blobs.</param>
    /// <param name="clock">UTC clock.</param>
    /// <exception cref="ArgumentNullException">When any required collaborator is <see langword="null"/>.</exception>
    public TemplateAdminService(
        IEnumerable<IDocxTemplate> templates,
        ICnasDbContext db,
        IFileStorage storage,
        ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);

        _templates = [.. templates];
        _db = db;
        _storage = storage;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<TemplateCatalogEntry>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        // Materialise the persistent rows first so the collision tiebreak runs against
        // a fixed snapshot. The Where clause is supported by the (Code, IsCurrent)
        // partial index registered in DocumentTemplateConfiguration.
        var persistentRows = await _db.DocumentTemplates
            .Where(t => t.IsCurrent && t.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var persistentEntries = persistentRows
            .Select(ProjectPersistent)
            .ToList();

        // Build a case-insensitive set of persistent codes so we can suppress the
        // DI-baked rows that collide. The collision rule is documented at class level:
        // persistent wins, DI is hidden, so the UI never shows two rows for one code.
        var persistentCodes = persistentRows
            .Select(r => r.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var diEntries = _templates
            .Where(t => !persistentCodes.Contains(t.TemplateCode))
            .Select(ProjectDi);

        IReadOnlyList<TemplateCatalogEntry> entries = persistentEntries
            .Concat(diEntries)
            .OrderBy(static e => e.Code, StringComparer.Ordinal)
            .ToList();

        return Result<IReadOnlyList<TemplateCatalogEntry>>.Success(entries);
    }

    /// <inheritdoc />
    public async Task<Result<TemplateCatalogEntry>> GetAsync(
        string templateCode,
        CancellationToken cancellationToken = default)
    {
        // Null / whitespace input is treated as "no match" rather than thrown — the
        // controller's route binding makes empty strings unlikely but the service must
        // be tolerant of malformed keys and surface a clean 404 via the standard
        // ErrorCodes.NotFound path.
        if (string.IsNullOrWhiteSpace(templateCode))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.NotFound,
                "Template code must not be null or whitespace.");
        }

        var canonical = templateCode.Trim().ToLowerInvariant();

        // Persistent first — operator override.
        var persistent = await _db.DocumentTemplates
            .Where(t => t.Code == canonical && t.IsCurrent && t.IsActive)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (persistent is not null)
        {
            return Result<TemplateCatalogEntry>.Success(ProjectPersistent(persistent));
        }

        // Fall through to the DI projection (case-insensitive — mirrors phase 1
        // semantics and DocumentGenerationService's renderer dispatch).
        foreach (var template in _templates)
        {
            if (string.Equals(template.TemplateCode, templateCode, StringComparison.OrdinalIgnoreCase))
            {
                return Result<TemplateCatalogEntry>.Success(ProjectDi(template));
            }
        }

        return Result<TemplateCatalogEntry>.Failure(
            ErrorCodes.NotFound,
            $"No template registered with code '{templateCode}'.");
    }

    /// <inheritdoc />
    public async Task<Result<TemplateCatalogEntry>> UploadAsync(
        string code,
        string name,
        string? description,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // ── Code shape validation. ──
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.ValidationFailed,
                "Template code must not be null or whitespace.");
        }

        // Trim surrounding whitespace, but do NOT lower-case before the regex check —
        // the kebab-case contract is strict: mixed case is a validation failure, not a
        // silent normalisation. The persisted row uses the trimmed (already-lower-case)
        // form once the shape has been validated.
        var canonicalCode = code.Trim();
        if (canonicalCode.Length > 96)
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.ValidationFailed,
                "Template code must be 96 characters or fewer.");
        }
        if (!CodeShapeRegex().IsMatch(canonicalCode))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.ValidationFailed,
                "Template code must be kebab-case (lower-case letters, digits, hyphens).");
        }

        // ── Name shape validation. ──
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.ValidationFailed,
                "Template name must not be null or whitespace.");
        }
        var trimmedName = name.Trim();
        if (trimmedName.Length > 256)
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.ValidationFailed,
                "Template name must be 256 characters or fewer.");
        }

        // ── MIME type exact-match. ──
        if (!string.Equals(contentType, DocxContentType, StringComparison.OrdinalIgnoreCase))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.FileTypeMismatch,
                $"Template content type must be '{DocxContentType}'.");
        }

        // ── Magic-byte sniff + buffered copy with SHA-256 computation + size cap. ──
        // We buffer to a MemoryStream so we can both compute SHA-256 deterministically
        // and replay the bytes into storage without depending on the input stream
        // being seekable (multipart form streams typically are not).
        using var buffer = new MemoryStream();
        var header = new byte[4];
        var headerRead = await content.ReadAsync(header.AsMemory(0, 4), ct).ConfigureAwait(false);
        if (headerRead < 4 || !header.AsSpan().SequenceEqual(DocxMagicBytes))
        {
            return Result<TemplateCatalogEntry>.Failure(
                ErrorCodes.FileTypeMismatch,
                "Template content does not start with the DOCX magic bytes (50 4B 03 04).");
        }
        await buffer.WriteAsync(header.AsMemory(0, headerRead), ct).ConfigureAwait(false);

        // Copy the remainder using a sized buffer so the size cap is enforced cheaply
        // — we stop reading the moment we exceed MaxTemplateSize. The cap is inclusive
        // of the header bytes already written above.
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaxTemplateSize)
            {
                return Result<TemplateCatalogEntry>.Failure(
                    ErrorCodes.FileTooLarge,
                    $"Template content exceeds the {MaxTemplateSize}-byte cap.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        // SHA-256 over the buffered bytes — the same digest will be persisted on the row
        // and used by callers to verify integrity off-band.
        buffer.Position = 0;
        var sha256Bytes = await SHA256.HashDataAsync(buffer, ct).ConfigureAwait(false);
        var sha256Hex = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
        var contentLength = buffer.Length;

        // ── Compute the next version under the canonical code. ──
        // The query is independent of IsCurrent (history rows count toward the max).
        var nextVersion = await _db.DocumentTemplates
            .Where(t => t.Code == canonicalCode)
            .Select(t => (int?)t.Version)
            .MaxAsync(ct)
            .ConfigureAwait(false) + 1 ?? 1;

        // ── Flip IsCurrent on the previous current row, if any. ──
        var previousCurrent = await _db.DocumentTemplates
            .Where(t => t.Code == canonicalCode && t.IsCurrent)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (previousCurrent is not null)
        {
            previousCurrent.IsCurrent = false;
            previousCurrent.UpdatedAtUtc = _clock.UtcNow;
        }

        // ── Push the binary to storage. The object key embeds code + version so the
        // MinIO console layout is human-navigable. ──
        var objectKey = $"templates/{canonicalCode}/v{nextVersion}/{canonicalCode}.docx";
        buffer.Position = 0;
        var stored = await _storage.PutAsync(TemplatesBucket, buffer, DocxContentType, ct).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return Result<TemplateCatalogEntry>.Failure(stored.ErrorCode!, stored.ErrorMessage!);
        }

        // The IFileStorage.PutAsync contract returns its own randomly-generated object
        // key; we keep the human-readable composed key on the row (for log-trace
        // navigation) but also remember the storage-side key for the actual download
        // lookup. In practice the two converge when the test/in-memory storage is
        // configured to accept the supplied key verbatim — see the InMemoryFileStorage
        // wiring in the E2E fixture. For MinIO we trust the storage-side key.
        var actualObjectKey = string.IsNullOrWhiteSpace(stored.Value.ObjectKey)
            ? objectKey
            : stored.Value.ObjectKey;

        // ── Insert the row. ──
        var row = new DocumentTemplate
        {
            Code = canonicalCode,
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Version = nextVersion,
            IsCurrent = true,
            StorageObjectKey = actualObjectKey,
            ContentType = DocxContentType,
            ContentLength = contentLength,
            ContentSha256 = sha256Hex,
            CreatedAtUtc = _clock.UtcNow,
            IsActive = true,
        };
        _db.DocumentTemplates.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<TemplateCatalogEntry>.Success(ProjectPersistent(row));
    }

    /// <inheritdoc />
    public async Task<Result<TemplateDownloadStream>> DownloadAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<TemplateDownloadStream>.Failure(
                ErrorCodes.NotFound,
                "Template code must not be null or whitespace.");
        }

        var canonical = code.Trim().ToLowerInvariant();

        var row = await _db.DocumentTemplates
            .Where(t => t.Code == canonical && t.IsCurrent && t.IsActive)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            // No persistent row. DI-baked templates have no stored blob and therefore
            // cannot be downloaded — phase 2B may revisit this once the renderer
            // pipeline is unified and uploaded templates render through the same
            // dispatch as the DI-baked ones. Until then, return NotFound regardless of
            // whether a DI-baked template carries the same code.
            return Result<TemplateDownloadStream>.Failure(
                ErrorCodes.NotFound,
                $"No persistent template registered with code '{code}'.");
        }

        var blob = await _storage.GetAsync(TemplatesBucket, row.StorageObjectKey, ct).ConfigureAwait(false);
        if (blob.IsFailure)
        {
            return Result<TemplateDownloadStream>.Failure(blob.ErrorCode!, blob.ErrorMessage!);
        }

        return Result<TemplateDownloadStream>.Success(new TemplateDownloadStream(
            Content: blob.Value,
            ContentType: row.ContentType,
            ContentLength: row.ContentLength,
            SuggestedFileName: $"{row.Code}.docx",
            Sha256: row.ContentSha256));
    }

    /// <summary>
    /// Projects a single DI-baked <see cref="IDocxTemplate"/> singleton into the catalog
    /// row contract with <c>Source = "DI"</c>. Reflection here is cheap —
    /// <see cref="Type.FullName"/> and <c>Assembly.GetName()</c> are both effectively
    /// constant lookups per type.
    /// </summary>
    /// <param name="template">A registered template instance.</param>
    /// <returns>The matching catalog row.</returns>
    private static TemplateCatalogEntry ProjectDi(IDocxTemplate template)
    {
        var clrType = template.GetType();
        return new TemplateCatalogEntry(
            Code: template.TemplateCode,
            ClrTypeFullName: clrType.FullName ?? clrType.Name,
            AssemblyName: clrType.Assembly.GetName().Name ?? string.Empty,
            Source: "DI");
    }

    /// <summary>
    /// Synthetic CLR-type marker used for persistent rows. Persistent templates have
    /// no compiled-in <see cref="IDocxTemplate"/> implementation; the catalog row's
    /// <see cref="TemplateCatalogEntry.ClrTypeFullName"/> field must still be
    /// non-empty so the phase-1 invariant "every row carries non-empty
    /// implementation metadata" continues to hold for downstream UI code that
    /// renders the column unconditionally. The <see cref="TemplateCatalogEntry.Source"/>
    /// discriminator is the authoritative signal that the row is operator-managed.
    /// </summary>
    private const string PersistentClrTypeMarker =
        "Cnas.Ps.Infrastructure.Documents.Templates.PersistentDocumentTemplate";

    /// <summary>
    /// Synthetic assembly-name marker used for persistent rows. See
    /// <see cref="PersistentClrTypeMarker"/> for the non-empty-contract rationale.
    /// </summary>
    private const string PersistentAssemblyMarker = "Cnas.Ps.Infrastructure.Persistent";

    /// <summary>
    /// Projects a persistent <see cref="DocumentTemplate"/> row into the catalog row
    /// contract with <c>Source = "Persistent"</c>. The CLR / assembly metadata fields
    /// carry synthetic marker strings (see <see cref="PersistentClrTypeMarker"/>) so
    /// the phase-1 invariant about non-empty implementation metadata continues to
    /// hold even though persistent templates have no compiled-in type. The
    /// <see cref="TemplateCatalogEntry.Source"/> field is the authoritative signal
    /// for "this row is operator-managed".
    /// </summary>
    /// <param name="row">The persisted row.</param>
    /// <returns>The matching catalog row.</returns>
    private static TemplateCatalogEntry ProjectPersistent(DocumentTemplate row)
    {
        return new TemplateCatalogEntry(
            Code: row.Code,
            ClrTypeFullName: PersistentClrTypeMarker,
            AssemblyName: PersistentAssemblyMarker,
            Source: "Persistent",
            Name: row.Name,
            Version: row.Version,
            ContentLength: row.ContentLength);
    }
}
