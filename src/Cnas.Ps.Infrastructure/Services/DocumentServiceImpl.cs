using System.Globalization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC11 — Download document. Also handles citizen uploads with magic-byte sniffing
/// (SEC 010 / CLAUDE.md §5.1) and the R0671-continuation paged registry list. Detached
/// signatures (SEC 057) are referenced from the same <see cref="Document"/> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>List pipeline.</b> <see cref="ListAsync"/> wires the R0163 QBE converter, the
/// R0167 query-budget guard, and the R0671 access-scope filter (Document-category
/// narrowing). The access-scope filter runs BEFORE the budget gate so the budget
/// evaluates the SCOPED row count — same security property documented on
/// <see cref="SolicitantService"/>.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="storage">Blob storage adapter (MinIO) for citizen uploads.</param>
/// <param name="storageOptions">MinIO configuration (bucket names + presign TTL).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies <c>UserSqid</c>, roles, AccessScope.</param>
/// <param name="budget">R0167 query-budget guard consulted before list materialisation.</param>
/// <param name="qbeConverter">R0163 QBE-to-LINQ converter used by the list path.</param>
/// <param name="accessScopeFilter">R0671 row-level access-scope predicate splicer.</param>
public sealed class DocumentServiceImpl(
    ICnasDbContext db,
    ISqidService sqids,
    IFileStorage storage,
    IOptions<Storage.MinioOptions> storageOptions,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IQueryBudgetService budget,
    IQbeToLinqConverter qbeConverter,
    IAccessScopeFilter accessScopeFilter) : IDocumentService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly IFileStorage _storage = storage;
    private readonly Storage.MinioOptions _storageOptions = storageOptions.Value;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IQueryBudgetService _budget = budget;
    private readonly IQbeToLinqConverter _qbeConverter = qbeConverter;
    private readonly IAccessScopeFilter _accessScopeFilter = accessScopeFilter;

    /// <inheritdoc />
    public QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    private static readonly Dictionary<string, byte[][]> KnownMagicBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = [[0x25, 0x50, 0x44, 0x46]],
        ["image/png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
        ["image/jpeg"] = [[0xFF, 0xD8, 0xFF, 0xE0], [0xFF, 0xD8, 0xFF, 0xE1], [0xFF, 0xD8, 0xFF, 0xDB]],
    };

    /// <inheritdoc />
    public async Task<Result<string>> UploadAsync(string fileName, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (_caller.UserId is null)
        {
            return Result<string>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // Magic-byte sniff (SEC 010 / CLAUDE.md §5.1).
        if (!KnownMagicBytes.TryGetValue(contentType, out var signatures))
        {
            return Result<string>.Failure(ErrorCodes.FileTypeMismatch, "Unsupported MIME type.");
        }

        var header = new byte[Math.Max(8, signatures.Max(s => s.Length))];
        var read = await content.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read < header.Length || !signatures.Any(sig => header.Take(sig.Length).SequenceEqual(sig)))
        {
            return Result<string>.Failure(ErrorCodes.FileTypeMismatch, "File signature does not match declared MIME type.");
        }

        // Reset stream to start (or chain header + remainder).
        Stream combined;
        if (content.CanSeek)
        {
            content.Position = 0;
            combined = content;
        }
        else
        {
            var ms = new MemoryStream();
            await ms.WriteAsync(header.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            combined = ms;
        }

        var stored = await _storage.PutAsync(_storageOptions.CitizenUploadsBucket, combined, contentType, cancellationToken).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return Result<string>.Failure(stored.ErrorCode!, stored.ErrorMessage!);
        }

        var doc = new Document
        {
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _caller.UserSqid,
            Kind = DocumentKind.Attachment,
            Title = Path.GetFileNameWithoutExtension(fileName),
            MimeType = contentType,
            SizeBytes = stored.Value.SizeBytes,
            StorageObjectKey = stored.Value.ObjectKey,
            StorageBucket = _storageOptions.CitizenUploadsBucket,
            ContentSha256Hex = stored.Value.ContentSha256Hex,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(doc.Id));
    }

    /// <inheritdoc />
    public async Task<Result<Uri>> GetDownloadUrlAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(documentId);
        if (decoded.IsFailure)
        {
            return Result<Uri>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var doc = await _db.Documents.SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return Result<Uri>.Failure(ErrorCodes.NotFound, "Document not found.");
        }

        // Ownership check — applicants only download their own dossier's documents.
        // CNAS staff with cnas-user role bypass this check (authorization audit captures the access).
        if (doc.DossierId is null && !_caller.Roles.Contains("cnas-user"))
        {
            return Result<Uri>.Failure(ErrorCodes.Forbidden, "Document has no downloadable owner scope.");
        }

        if (doc.DossierId is not null && !_caller.Roles.Contains("cnas-user") && _caller.UserId is not null)
        {
            var ownedByCaller = await _db.Dossiers
                .Where(d => d.Id == doc.DossierId.Value)
                .Join(_db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => a.SolicitantId)
                .AnyAsync(s => s == _caller.UserId.Value, cancellationToken).ConfigureAwait(false);
            if (!ownedByCaller)
            {
                return Result<Uri>.Failure(ErrorCodes.Forbidden, "Not your document.");
            }
        }

        return await _storage.PresignDownloadAsync(doc.StorageBucket, doc.StorageObjectKey, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<DocumentsListPageDto>> ListAsync(
        DocumentsListInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        LastBudgetVerdict = null;

        // 1. Envelope validation. Catches Take > 200 / Skip < 0 / FromUtc > ToUtc
        //    before the DB scope opens. The controller pre-runs the validator too;
        //    keeping it here makes the service safe to call from background flows.
        var validator = new DocumentsListInputValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<DocumentsListPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // 2. Build the filtered queryable. The pipeline order is:
        //      base predicate (IsActive) →
        //      access-scope filter (R0671, BEFORE budget so budget sees scoped count) →
        //      QBE narrowing (R0163) →
        //      date-range bounds.
        IQueryable<Document> query = _db.Documents.Where(d => d.IsActive);
        query = _accessScopeFilter.ApplyToDocuments(query, _caller.AccessScope);

        var ctx = new QueryFilterContext();

        if (input.Filter is { Conditions.Count: > 0 } dto)
        {
            var qbe = MapQbe(dto);
            var converted = _qbeConverter.Convert<Document>(QueryBudgetRegistries.Document, qbe);
            if (converted.IsFailure)
            {
                return Result<DocumentsListPageDto>.Failure(converted.ErrorCode!, converted.ErrorMessage!);
            }
            query = query.Where(converted.Value);
            ctx = ctx.With("Qbe", dto.Conditions.Count.ToString(CultureInfo.InvariantCulture));
            // Hoist Q-substituting predicates into the budget context so the
            // policy's "AddFreeTextFilter / AddOwnerFilter" suppression matches.
            foreach (var cond in dto.Conditions)
            {
                if (string.Equals(cond.FieldName, "DossierId", StringComparison.Ordinal))
                {
                    ctx = ctx.With("OwnerSolicitantId", cond.Value ?? string.Empty);
                }
            }
        }

        if (input.FromUtc is { } from)
        {
            ctx = ctx.With("CreatedFromUtc", from);
            query = query.Where(d => d.CreatedAtUtc >= from);
        }
        if (input.ToUtc is { } to)
        {
            ctx = ctx.With("CreatedToUtc", to);
            query = query.Where(d => d.CreatedAtUtc < to);
        }

        // 3. Budget gate. Verdict cached on LastBudgetVerdict so the controller can
        //    surface it on the 422 ProblemDetails extension bag.
        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.Document,
            query,
            ctx,
            cancellationToken).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<DocumentsListPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 4. Paging window. Take is already capped by the validator so the
        //    materialised list is bounded.
        var skip = Math.Max(0, input.Skip);
        var take = Math.Clamp(input.Take, 1, DocumentsListInputValidator.MaxTake);

        var rows = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .ThenByDescending(d => d.Id)
            .Skip(skip)
            .Take(take)
            .Select(d => new
            {
                d.Id,
                d.DossierId,
                d.Kind,
                d.Title,
                d.MimeType,
                d.SizeBytes,
                d.CreatedAtUtc,
                d.CreatedBy,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => new DocumentListItemDto(
                Id: _sqids.Encode(r.Id),
                OwnerEntityType: r.DossierId is null ? null : "Dossier",
                OwnerEntitySqid: r.DossierId is { } owner ? _sqids.Encode(owner) : null,
                DocumentKind: r.Kind.ToString(),
                FileName: r.Title,
                MimeType: r.MimeType,
                SizeBytes: r.SizeBytes,
                CreatedAtUtc: r.CreatedAtUtc,
                IssuedByUserSqid: r.CreatedBy))
            .ToList();

        return Result<DocumentsListPageDto>.Success(new DocumentsListPageDto(
            items,
            (int)Math.Min(int.MaxValue, verdict.EstimatedRowCount)));
    }

    /// <summary>
    /// Translates a wire <see cref="QbeFilterDto"/> to the server-side
    /// <see cref="QbeFilter"/>. Operator strings that fail to parse surface as a
    /// sentinel value the converter rejects with a stable
    /// <see cref="ErrorCodes.QbeOperatorNotSupported"/> code.
    /// </summary>
    /// <param name="dto">Wire envelope.</param>
    /// <returns>Mapped server-side filter.</returns>
    private static QbeFilter MapQbe(QbeFilterDto dto)
    {
        var conds = new List<QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                op = (QbeOperator)int.MinValue;
            }
            conds.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conds);
    }
}
