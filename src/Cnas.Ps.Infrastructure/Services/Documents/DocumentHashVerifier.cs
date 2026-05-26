using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Documents;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — production implementation of
/// <see cref="IDocumentHashVerifier"/>. Reads the document row, downloads the
/// underlying bytes via <see cref="IFileStorage"/>, computes a fresh SHA-256,
/// and compares against the recorded <c>ContentSha256Hex</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metrics.</b> Every call emits an audit row — Information
/// severity on match, Critical on mismatch — and increments
/// <c>cnas.document.hash_verify</c> tagged with
/// <c>outcome={match|mismatch|error}</c>.
/// </para>
/// </remarks>
public sealed class DocumentHashVerifier : IDocumentHashVerifier
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyCnasDbContext _read;
    private readonly IFileStorage _storage;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ILogger<DocumentHashVerifier> _logger;

    /// <summary>Constructs the verifier with its scoped collaborators.</summary>
    /// <param name="read">Read-replica context (the verifier never mutates).</param>
    /// <param name="storage">File-storage abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    public DocumentHashVerifier(
        IReadOnlyCnasDbContext read,
        IFileStorage storage,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        ILogger<DocumentHashVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _read = read;
        _storage = storage;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<DocumentHashVerificationDto>> VerifyAsync(
        string documentSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(documentSqid);
        if (decoded.IsFailure)
        {
            return Result<DocumentHashVerificationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var doc = await _read.Documents
            .FirstOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<DocumentHashVerificationDto>.Failure(
                ErrorCodes.NotFound, "Document not found.");
        }

        var actor = _caller.UserSqid ?? "admin";
        var stored = doc.ContentSha256Hex ?? string.Empty;
        var fetch = await _storage.GetAsync(doc.StorageBucket, doc.StorageObjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (fetch.IsFailure)
        {
            CnasMeter.DocumentHashVerification.Add(
                1, new KeyValuePair<string, object?>("outcome", "error"));
            await EmitAuditAsync(doc, stored, computed: null, isMatch: false, isError: true, actor,
                cancellationToken).ConfigureAwait(false);
            return Result<DocumentHashVerificationDto>.Failure(fetch.ErrorCode!, fetch.ErrorMessage!);
        }

        string computed;
        await using (fetch.Value.ConfigureAwait(false))
        {
            computed = await ComputeSha256HexAsync(fetch.Value, cancellationToken).ConfigureAwait(false);
        }

        var isMatch = string.Equals(stored, computed, StringComparison.OrdinalIgnoreCase);
        CnasMeter.DocumentHashVerification.Add(
            1, new KeyValuePair<string, object?>("outcome", isMatch ? "match" : "mismatch"));

        await EmitAuditAsync(doc, stored, computed, isMatch, isError: false, actor, cancellationToken)
            .ConfigureAwait(false);

        if (!isMatch)
        {
            _logger.LogCritical(
                "Document hash MISMATCH for {DocumentId} — storage corruption or tampering suspected.",
                doc.Id);
        }

        return Result<DocumentHashVerificationDto>.Success(new DocumentHashVerificationDto(
            DocumentSqid: _sqids.Encode(doc.Id),
            IsMatch: isMatch,
            StoredHash: stored,
            ComputedHash: computed));
    }

    /// <summary>
    /// Streams <paramref name="stream"/> end-to-end through a SHA-256 hasher
    /// and returns the lower-case hex digest.
    /// </summary>
    /// <param name="stream">Open, readable byte stream.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>64-character lower-case hex SHA-256.</returns>
    private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder(digest.Length * 2);
        for (int i = 0; i < digest.Length; i++)
        {
            sb.Append(digest[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Emits the lifecycle audit row with a PII-free details payload.</summary>
    /// <param name="doc">Document row.</param>
    /// <param name="storedHash">Stored hash recorded on the document.</param>
    /// <param name="computed">Freshly computed hash (null on storage error).</param>
    /// <param name="isMatch">Match outcome.</param>
    /// <param name="isError">True when the storage fetch itself failed.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>A completed Task.</returns>
    private Task EmitAuditAsync(
        Document doc,
        string storedHash,
        string? computed,
        bool isMatch,
        bool isError,
        string actor,
        CancellationToken cancellationToken)
    {
        var severity = isError
            ? AuditSeverity.Critical
            : (isMatch ? AuditSeverity.Information : AuditSeverity.Critical);
        var payload = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(doc.Id),
            isMatch,
            isError,
            storedHash,
            computedHash = computed,
        }, CachedJsonOptions);
        return _audit.RecordAsync(
            IDocumentHashVerifier.AuditHashVerify,
            severity,
            actor,
            nameof(Document),
            doc.Id,
            payload,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken);
    }
}
