using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R2161 / TOR INT 002 — production implementation of
/// <see cref="IOfflineBatchService"/>. Persists an
/// <see cref="OfflineBatchJob"/> row per submission, emits a Critical audit
/// event, and leaves the asynchronous processing to the iter-79
/// <c>OfflineBatchProcessor</c> infrastructure (the Quartz job picks the
/// oldest <c>Pending</c> row per fire). This service intentionally does NOT
/// own the trigger-scheduling primitive — submissions are picked up by the
/// existing <c>OfflineBatchProcessingJob</c> sweep, which is the simplest
/// "one-shot trigger" semantic that survives a pod restart without losing
/// in-flight rows.
/// </summary>
public sealed class OfflineBatchService : IOfflineBatchService
{
    /// <summary>Cached JSON serialiser options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="clock">UTC clock abstraction (RULE — never call <c>DateTime.UtcNow</c> directly).</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution and per-caller scoping.</param>
    /// <param name="audit">Audit-journal façade.</param>
    public OfflineBatchService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public Task<Result<OfflineBatchJobDto>> SubmitIngestAsync(
        OfflineBatchIngestInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SubmitAsync(
            OfflineBatchJobKind.Ingest,
            input.Description,
            input.Rows,
            IOfflineBatchService.AuditIngestSubmitted,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<OfflineBatchJobDto>> SubmitExportAsync(
        OfflineBatchExportInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SubmitAsync(
            OfflineBatchJobKind.Export,
            input.Description,
            input.Filters,
            IOfflineBatchService.AuditExportSubmitted,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchJobDto>> GetStatusAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchJobDto>.Failure(
                decoded.ErrorCode!,
                decoded.ErrorMessage!);
        }
        var id = decoded.Value;

        // Per-caller scoping — anonymous / system callers cannot reach this
        // path because the controller is [Authorize], but the predicate is
        // belt-and-braces against a future internal misuse.
        var callerId = _caller.UserId ?? 0;
        var row = await _db.OfflineBatchJobs
            .FirstOrDefaultAsync(
                j => j.Id == id
                    && j.IsActive
                    && j.SubmittedByUserId == callerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<OfflineBatchJobDto>.Failure(
                ErrorCodes.NotFound,
                "Offline batch job not found.");
        }
        return Result<OfflineBatchJobDto>.Success(ToDto(row));
    }

    /// <summary>
    /// Shared submission path for ingest + export. Validates the row count
    /// against <see cref="IOfflineBatchService.MaxRows"/>, persists a
    /// <see cref="OfflineBatchJobStatus.Pending"/> row, and emits the audit
    /// event identified by <paramref name="auditCode"/>.
    /// </summary>
    /// <param name="kind">Discriminator between ingest and export jobs.</param>
    /// <param name="description">Optional caller-supplied description (1..256 chars).</param>
    /// <param name="rows">Multi-record payload to size-check.</param>
    /// <param name="auditCode">Audit event code emitted on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outbound projection of the persisted row, or a failure code.</returns>
    private async Task<Result<OfflineBatchJobDto>> SubmitAsync(
        OfflineBatchJobKind kind,
        string? description,
        IReadOnlyList<string>? rows,
        string auditCode,
        CancellationToken cancellationToken)
    {
        var rowList = rows ?? Array.Empty<string>();
        if (rowList.Count == 0)
        {
            return Result<OfflineBatchJobDto>.Failure(
                ErrorCodes.ValidationFailed,
                "At least one row is required.");
        }
        if (rowList.Count > IOfflineBatchService.MaxRows)
        {
            return Result<OfflineBatchJobDto>.Failure(
                IOfflineBatchService.PayloadTooLargeCode,
                $"Offline batch payload exceeds the {IOfflineBatchService.MaxRows}-row cap.");
        }
        if (description is { Length: > 256 })
        {
            return Result<OfflineBatchJobDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Description must be 256 characters or fewer.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var callerId = _caller.UserId ?? 0;
        var job = new OfflineBatchJob
        {
            Kind = kind,
            Status = OfflineBatchJobStatus.Pending,
            SubmittedByUserId = callerId,
            SubmittedAtUtc = now,
            RowCount = rowList.Count,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.OfflineBatchJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            auditCode,
            actor,
            job.Id,
            new
            {
                jobSqid = _sqids.Encode(job.Id),
                kind = kind.ToString(),
                rowCount = rowList.Count,
                hasDescription = !string.IsNullOrWhiteSpace(description),
                submittedAtUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<OfflineBatchJobDto>.Success(ToDto(job));
    }

    /// <summary>Writes a single Critical-severity audit row with a serialised payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            actor,
            nameof(OfflineBatchJob),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects the persisted row into its outbound DTO.</summary>
    /// <param name="j">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private OfflineBatchJobDto ToDto(OfflineBatchJob j) => new(
        Id: _sqids.Encode(j.Id),
        Kind: j.Kind.ToString(),
        Status: j.Status.ToString(),
        SubmittedAtUtc: j.SubmittedAtUtc,
        StartedAtUtc: j.StartedAtUtc,
        CompletedAtUtc: j.CompletedAtUtc,
        ErrorMessage: j.ErrorMessage,
        ResultBlobKey: j.ResultBlobKey,
        RowCount: j.RowCount);
}
