using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — production implementation of
/// <see cref="IOfflineBatchSubmissionService"/>. Persists request files,
/// parses rows, manages lifecycle transitions, and surfaces the registry
/// to consumer + admin clients.
/// </summary>
/// <remarks>
/// <para>
/// <b>No PII on audit rows.</b> The submission service writes audit rows
/// carrying the consumer subject + the Sqid + counters only. Per-row
/// payloads are NEVER serialised into audit rows.
/// </para>
/// </remarks>
public sealed class OfflineBatchSubmissionService : IOfflineBatchSubmissionService
{
    /// <summary>Stable audit event code emitted on a successful submission.</summary>
    public const string AuditBatchSubmitted = "BATCH.SUBMITTED";

    /// <summary>Stable audit event code emitted when a batch is cancelled.</summary>
    public const string AuditBatchCancelled = "BATCH.CANCELLED";

    /// <summary>Stable audit event code emitted when the download endpoint is hit.</summary>
    public const string AuditBatchDownloaded = "BATCH.DOWNLOADED";

    /// <summary>Stable conflict message when cancelling a non-cancellable batch.</summary>
    public const string CancelNotAllowedMessage = "BATCH_NOT_CANCELLABLE";

    /// <summary>Stable conflict message when the file hash supplied does not match the bytes.</summary>
    public const string FileHashMismatchMessage = "BATCH_FILE_HASH_MISMATCH";

    /// <summary>Stable conflict message when the download is requested on a non-Completed submission.</summary>
    public const string DownloadNotReadyMessage = "BATCH_NOT_COMPLETED";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IOfflineBatchBlobStore _blobs;
    private readonly IOfflineBatchRequestParser _parser;
    private readonly IValidator<OfflineBatchSubmissionInputDto> _submitValidator;
    private readonly IValidator<OfflineBatchReasonInputDto> _reasonValidator;
    private readonly IValidator<OfflineBatchSubmissionFilterDto> _listFilterValidator;
    private readonly IValidator<OfflineBatchRowFilterDto> _rowFilterValidator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder / decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit façade.</param>
    /// <param name="blobs">Byte-blob storage abstraction.</param>
    /// <param name="parser">Request-CSV parser.</param>
    /// <param name="submitValidator">Submission-input validator.</param>
    /// <param name="reasonValidator">Cancel-reason validator.</param>
    /// <param name="listFilterValidator">List-filter validator.</param>
    /// <param name="rowFilterValidator">Row-filter validator.</param>
    public OfflineBatchSubmissionService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IOfflineBatchBlobStore blobs,
        IOfflineBatchRequestParser parser,
        IValidator<OfflineBatchSubmissionInputDto> submitValidator,
        IValidator<OfflineBatchReasonInputDto> reasonValidator,
        IValidator<OfflineBatchSubmissionFilterDto> listFilterValidator,
        IValidator<OfflineBatchRowFilterDto> rowFilterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(submitValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(listFilterValidator);
        ArgumentNullException.ThrowIfNull(rowFilterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _blobs = blobs;
        _parser = parser;
        _submitValidator = submitValidator;
        _reasonValidator = reasonValidator;
        _listFilterValidator = listFilterValidator;
        _rowFilterValidator = rowFilterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchSubmissionDto>> SubmitAsync(
        OfflineBatchSubmissionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _submitValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        // Verify the file-hash matches the byte payload — protects against
        // transport corruption + double-uploads from races on the consumer
        // side.
        var computedHash = ComputeSha256Hex(input.RequestFileBytes);
        if (!string.Equals(computedHash, input.RequestFileHashSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result<OfflineBatchSubmissionDto>.Failure(
                ErrorCodes.ValidationFailed, FileHashMismatchMessage);
        }

        var opCode = Enum.Parse<AnnexFourBatchOp>(input.OpCode);
        var now = _clock.UtcNow;

        // Persist the request CSV — the blob store gives us an opaque key
        // we can resolve later in the processor.
        var storageKey = await _blobs
            .PutAsync(input.RequestFileBytes, "text/csv", cancellationToken)
            .ConfigureAwait(false);

        // Parse rows up-front so the consumer immediately sees a row count
        // and we can record per-row state for downstream lookups.
        using var stream = new MemoryStream(input.RequestFileBytes, writable: false);
        var parseResult = await _parser.ParseAsync(opCode, stream, cancellationToken).ConfigureAwait(false);
        if (parseResult.IsFailure)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(parseResult.ErrorCode!, parseResult.ErrorMessage!);
        }

        var seeds = parseResult.Value;
        var batchNumber = await GenerateBatchNumberAsync(now.Year, cancellationToken).ConfigureAwait(false);

        var submission = new OfflineBatchSubmission
        {
            BatchNumber = batchNumber,
            ConsumerSubject = input.ConsumerSubject,
            OpCode = opCode,
            Status = OfflineBatchStatus.Submitted,
            RequestFileName = input.RequestFileName,
            RequestFileSizeBytes = input.RequestFileBytes.LongLength,
            RequestFileHashSha256 = computedHash,
            RequestFileStorageKey = storageKey,
            RequestRowCount = seeds.Count,
            SubmittedAt = now,
            TotalRowsProcessed = 0,
            TotalRowsFailed = 0,
            CreatedAtUtc = now,
            CreatedBy = input.ConsumerSubject,
            IsActive = true,
        };
        _db.OfflineBatchSubmissions.Add(submission);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Persist per-row seeds. Parser-flagged failures land as pre-failed
        // rows so the processor records them as Failed without invoking the
        // synchronous API.
        foreach (var seed in seeds)
        {
            var row = new OfflineBatchRow
            {
                SubmissionId = submission.Id,
                RowOrdinal = seed.RowOrdinal,
                Status = seed.ParseError is null ? OfflineBatchRowStatus.Pending : OfflineBatchRowStatus.Failed,
                RequestPayloadJson = seed.RequestPayloadJson,
                ErrorCode = seed.ParseError?.ErrorCode,
                ErrorDescription = seed.ParseError?.ErrorDescription,
                ProcessedAt = seed.ParseError is null ? null : now,
                CreatedAtUtc = now,
                CreatedBy = input.ConsumerSubject,
                IsActive = true,
            };
            _db.OfflineBatchRows.Add(row);
        }

        submission.Status = OfflineBatchStatus.Queued;
        submission.UpdatedAtUtc = now;
        submission.UpdatedBy = input.ConsumerSubject;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.OfflineBatchSubmitted.Add(1,
            new KeyValuePair<string, object?>("op_code", opCode.ToString()));

        await _audit.RecordAsync(
            AuditBatchSubmitted,
            AuditSeverity.Information,
            actorId: input.ConsumerSubject,
            targetEntity: nameof(OfflineBatchSubmission),
            targetEntityId: submission.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                batchNumber,
                opCode = opCode.ToString(),
                rowCount = seeds.Count,
                fileSizeBytes = submission.RequestFileSizeBytes,
            }),
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<OfflineBatchSubmissionDto>.Success(ToDto(submission));
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchSubmissionDto>> CancelAsync(
        string sqid,
        OfflineBatchReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var sub = await _db.OfflineBatchSubmissions
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (sub is null)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        if (sub.Status is not (OfflineBatchStatus.Submitted or OfflineBatchStatus.Queued))
        {
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.Conflict, CancelNotAllowedMessage);
        }

        var actor = _caller.UserSqid ?? sub.ConsumerSubject;
        var now = _clock.UtcNow;
        sub.Status = OfflineBatchStatus.Cancelled;
        sub.CompletedAt = now;
        sub.FailureReason = input.Reason.Length > 1000 ? input.Reason[..1000] : input.Reason;
        sub.UpdatedAtUtc = now;
        sub.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditBatchCancelled,
            AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(OfflineBatchSubmission),
            targetEntityId: sub.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                batchNumber = sub.BatchNumber,
                consumerSubject = sub.ConsumerSubject,
                reasonLength = input.Reason.Length,
            }),
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<OfflineBatchSubmissionDto>.Success(ToDto(sub));
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchSubmissionDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var sub = await _db.OfflineBatchSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (sub is null)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        return Result<OfflineBatchSubmissionDto>.Success(ToDto(sub));
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchDownloadInfoDto>> GetDownloadInfoAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchDownloadInfoDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var sub = await _db.OfflineBatchSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (sub is null)
        {
            return Result<OfflineBatchDownloadInfoDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        if (sub.Status != OfflineBatchStatus.Completed
            || sub.ResponseFileStorageKey is null
            || sub.ResponseFileHashSha256 is null
            || sub.ResponseFileSignatureBase64 is null
            || sub.CompletedAt is null)
        {
            return Result<OfflineBatchDownloadInfoDto>.Failure(ErrorCodes.Conflict, DownloadNotReadyMessage);
        }
        var bytes = await _blobs.GetAsync(sub.ResponseFileStorageKey, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditBatchDownloaded,
            AuditSeverity.Information,
            actorId: _caller.UserSqid ?? sub.ConsumerSubject,
            targetEntity: nameof(OfflineBatchSubmission),
            targetEntityId: sub.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                batchNumber = sub.BatchNumber,
                consumerSubject = sub.ConsumerSubject,
                sizeBytes = bytes.LongLength,
            }),
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        var info = new OfflineBatchDownloadInfoDto(
            DownloadUrl: $"/api/interop/batch/submissions/{sqid}/download",
            FileName: $"{sub.BatchNumber}-response.csv",
            ContentType: "text/csv",
            SizeBytes: bytes.LongLength,
            HashSha256: sub.ResponseFileHashSha256,
            SignatureBase64: sub.ResponseFileSignatureBase64,
            SignedAt: sub.CompletedAt.Value);
        return Result<OfflineBatchDownloadInfoDto>.Success(info);
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchDownloadBytesDto>> GetDownloadBytesAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchDownloadBytesDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var sub = await _db.OfflineBatchSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (sub is null)
        {
            return Result<OfflineBatchDownloadBytesDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        if (sub.Status != OfflineBatchStatus.Completed
            || sub.ResponseFileStorageKey is null
            || sub.ResponseFileHashSha256 is null
            || sub.ResponseFileSignatureBase64 is null
            || sub.CompletedAt is null)
        {
            return Result<OfflineBatchDownloadBytesDto>.Failure(ErrorCodes.Conflict, DownloadNotReadyMessage);
        }
        var bytes = await _blobs.GetAsync(sub.ResponseFileStorageKey, cancellationToken).ConfigureAwait(false);

        var info = new OfflineBatchDownloadInfoDto(
            DownloadUrl: $"/api/interop/batch/submissions/{sqid}/download",
            FileName: $"{sub.BatchNumber}-response.csv",
            ContentType: "text/csv",
            SizeBytes: bytes.LongLength,
            HashSha256: sub.ResponseFileHashSha256,
            SignatureBase64: sub.ResponseFileSignatureBase64,
            SignedAt: sub.CompletedAt.Value);
        return Result<OfflineBatchDownloadBytesDto>.Success(new OfflineBatchDownloadBytesDto(info, bytes));
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchSubmissionPageDto>> ListAsync(
        OfflineBatchSubmissionFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _listFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<OfflineBatchSubmissionPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        IQueryable<OfflineBatchSubmission> q = _db.OfflineBatchSubmissions.AsNoTracking().Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.ConsumerSubject))
        {
            q = q.Where(s => s.ConsumerSubject == filter.ConsumerSubject);
        }
        if (!string.IsNullOrWhiteSpace(filter.OpCode)
            && Enum.TryParse<AnnexFourBatchOp>(filter.OpCode, ignoreCase: false, out var op))
        {
            q = q.Where(s => s.OpCode == op);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<OfflineBatchStatus>(filter.Status, ignoreCase: false, out var st))
        {
            q = q.Where(s => s.Status == st);
        }
        if (filter.SubmittedAfter is { } after)
        {
            q = q.Where(s => s.SubmittedAt >= after);
        }
        if (filter.SubmittedBefore is { } before)
        {
            q = q.Where(s => s.SubmittedAt <= before);
        }
        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(s => s.SubmittedAt)
            .ThenByDescending(s => s.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result<OfflineBatchSubmissionPageDto>.Success(new OfflineBatchSubmissionPageDto(
            rows.Select(ToDto).ToList(),
            total,
            filter.Skip,
            filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchRowPageDto>> ListRowsAsync(
        string sqid,
        OfflineBatchRowFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _rowFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<OfflineBatchRowPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchRowPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var subExists = await _db.OfflineBatchSubmissions
            .AsNoTracking()
            .AnyAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (!subExists)
        {
            return Result<OfflineBatchRowPageDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        IQueryable<OfflineBatchRow> q = _db.OfflineBatchRows.AsNoTracking()
            .Where(r => r.SubmissionId == decoded.Value && r.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<OfflineBatchRowStatus>(filter.Status, ignoreCase: false, out var st))
        {
            q = q.Where(r => r.Status == st);
        }
        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderBy(r => r.RowOrdinal)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result<OfflineBatchRowPageDto>.Success(new OfflineBatchRowPageDto(
            rows.Select(ToDto).ToList(),
            total,
            filter.Skip,
            filter.Take));
    }

    /// <summary>Maps an entity to the outbound DTO.</summary>
    /// <param name="sub">Entity row.</param>
    /// <returns>Outbound DTO.</returns>
    private OfflineBatchSubmissionDto ToDto(OfflineBatchSubmission sub)
        => new(
            Id: _sqids.Encode(sub.Id),
            BatchNumber: sub.BatchNumber,
            ConsumerSubject: sub.ConsumerSubject,
            OpCode: sub.OpCode.ToString(),
            Status: sub.Status.ToString(),
            RequestFileName: sub.RequestFileName,
            RequestFileSizeBytes: sub.RequestFileSizeBytes,
            RequestFileHashSha256: sub.RequestFileHashSha256,
            RequestRowCount: sub.RequestRowCount,
            ResponseFileHashSha256: sub.ResponseFileHashSha256,
            ResponseFileSignatureBase64: sub.ResponseFileSignatureBase64,
            SubmittedAt: sub.SubmittedAt,
            StartedAt: sub.StartedAt,
            CompletedAt: sub.CompletedAt,
            FailureReason: sub.FailureReason,
            TotalRowsProcessed: sub.TotalRowsProcessed,
            TotalRowsFailed: sub.TotalRowsFailed);

    /// <summary>Maps a row entity to its outbound DTO.</summary>
    /// <param name="row">Row entity.</param>
    /// <returns>Outbound DTO.</returns>
    private static OfflineBatchRowDto ToDto(OfflineBatchRow row)
        => new(
            RowOrdinal: row.RowOrdinal,
            Status: row.Status.ToString(),
            ErrorCode: row.ErrorCode,
            ErrorDescription: row.ErrorDescription,
            ProcessedAt: row.ProcessedAt);

    /// <summary>
    /// Builds the next batch number for the supplied year. Uses the
    /// running max sequence per year so re-fires within the same calendar
    /// year monotonically increment.
    /// </summary>
    /// <param name="year">Year segment of the batch number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stable batch number string.</returns>
    private async Task<string> GenerateBatchNumberAsync(int year, CancellationToken ct)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"OBS-{year}-");
        var existing = await _db.OfflineBatchSubmissions
            .Where(s => s.BatchNumber.StartsWith(prefix))
            .Select(s => s.BatchNumber)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        int maxSeq = 0;
        foreach (var n in existing)
        {
            if (n.Length > prefix.Length
                && int.TryParse(n[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            {
                if (seq > maxSeq) { maxSeq = seq; }
            }
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"OBS-{year}-{maxSeq + 1:D6}");
    }

    /// <summary>Hex-encoded SHA-256 of the supplied bytes (lower-case).</summary>
    /// <param name="bytes">Source bytes.</param>
    /// <returns>64-character lower-case hex digest.</returns>
    public static string ComputeSha256Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var digest = SHA256.HashData(bytes);
        var sb = new StringBuilder(digest.Length * 2);
        for (int i = 0; i < digest.Length; i++)
        {
            sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
