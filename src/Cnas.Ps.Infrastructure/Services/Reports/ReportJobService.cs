using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reports;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — default <see cref="IReportJobService"/>
/// implementation backed by <see cref="ICnasDbContext"/>. Implements the
/// user-driven half of the background report runner (enqueue / get / list /
/// cancel); the drain half lives in <see cref="ReportJobRunner"/> and the
/// Quartz <see cref="Jobs.ReportJobBackgroundJob"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit shape.</b> The service emits
/// <c>REPORT.JOB.ENQUEUED</c> (on create) and
/// <c>REPORT.JOB.CANCELLED</c> (on cancel) audit rows at
/// <see cref="AuditSeverity.Information"/>. <c>DetailsJson</c> carries the
/// stable identifying fields (template Sqid, format, status) only — the
/// payload is intentionally PII-free.
/// </para>
/// </remarks>
public sealed class ReportJobService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit)
    : IReportJobService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;

    /// <summary>Stable audit-event prefix for the user-driven lifecycle transitions.</summary>
    internal const string AuditPrefix = "REPORT.JOB";

    /// <summary>
    /// Stable failure message returned when <see cref="CancelAsync"/> is called
    /// against a non-Queued row. Kept as a constant so the API + tests can
    /// match exactly without spelling magic strings.
    /// </summary>
    public const string JobNotCancellableMessage = "JOB_NOT_CANCELLABLE";

    /// <inheritdoc />
    public async Task<Result<ReportJobDto>> EnqueueAsync(
        ReportJobEnqueueDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_caller.UserId is not long requesterId)
        {
            return Result<ReportJobDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Sqid → numeric template id. Surfaces INVALID_SQID for unparseable values.
        var decoded = _sqids.TryDecode(input.ReportTemplateSqid);
        if (decoded.IsFailure)
        {
            return Result<ReportJobDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Parse stable format name. The wire validator runs the same parse before
        // we get here, but defending in depth keeps the service callable from
        // background contexts that bypass the validator (e.g. cron-driven runs).
        if (!Enum.TryParse<ExportFormat>(input.Format, ignoreCase: false, out var format))
        {
            return Result<ReportJobDto>.Failure(
                ErrorCodes.ValidationFailed, $"Unknown export format '{input.Format}'.");
        }

        // The template must exist + be active; the caller does NOT need to be the
        // template owner — anybody with view access (i.e. the template is shared,
        // or they ARE the owner) may queue a background run.
        var template = await _db.ReportTemplates
            .Where(t => t.Id == decoded.Value && t.IsActive)
            .Select(t => new { t.Id, t.OwnerUserId, t.IsShared })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return Result<ReportJobDto>.Failure(ErrorCodes.NotFound, "Report template not found.");
        }
        if (template.OwnerUserId != requesterId && !template.IsShared)
        {
            return Result<ReportJobDto>.Failure(
                ErrorCodes.Forbidden, "Template is private to its owner.");
        }

        var now = _clock.UtcNow;
        var row = new ReportJob
        {
            ReportTemplateId = template.Id,
            RequestedByUserId = requesterId,
            Format = (int)format,
            Status = ReportJobStatus.Queued,
            QueuedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ReportJobs.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.ENQUEUED", row, cancellationToken).ConfigureAwait(false);

        return Result<ReportJobDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<ReportJobDto>> GetAsync(
        long jobId,
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result<ReportJobDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var row = await _db.ReportJobs
            .SingleOrDefaultAsync(j => j.Id == jobId && j.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ReportJobDto>.Failure(ErrorCodes.NotFound, "Report job not found.");
        }
        if (row.RequestedByUserId != callerId)
        {
            return Result<ReportJobDto>.Failure(
                ErrorCodes.Forbidden, "Job belongs to another user.");
        }

        return Result<ReportJobDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportJobDto>> ListForCurrentUserAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Array.Empty<ReportJobDto>();
        }

        var clamped = Math.Clamp(take, 1, 100);
        var rows = await _db.ReportJobs
            .Where(j => j.RequestedByUserId == callerId && j.IsActive)
            .OrderByDescending(j => j.QueuedAtUtc)
            .Take(clamped)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(Project).ToList();
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long jobId,
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var row = await _db.ReportJobs
            .SingleOrDefaultAsync(j => j.Id == jobId && j.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Report job not found.");
        }
        if (row.RequestedByUserId != callerId)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Job belongs to another user.");
        }
        if (row.Status != ReportJobStatus.Queued)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, JobNotCancellableMessage);
        }

        var now = _clock.UtcNow;
        row.Status = ReportJobStatus.Cancelled;
        row.CompletedAtUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.CANCELLED", row, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Projects a persisted <see cref="ReportJob"/> row to its public DTO
    /// form (Sqid-encoded ids, stable enum-name strings).
    /// </summary>
    /// <param name="row">Persisted row; non-null.</param>
    /// <returns>The DTO surface.</returns>
    internal ReportJobDto Project(ReportJob row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var format = ((ExportFormat)row.Format).ToString();
        return new ReportJobDto(
            Id: _sqids.Encode(row.Id),
            ReportTemplateSqid: _sqids.Encode(row.ReportTemplateId),
            RequestedByUserSqid: _sqids.Encode(row.RequestedByUserId),
            Format: format,
            Status: row.Status.ToString(),
            QueuedAtUtc: row.QueuedAtUtc,
            StartedAtUtc: row.StartedAtUtc,
            CompletedAtUtc: row.CompletedAtUtc,
            AttachmentSqid: row.AttachmentRecordId is long aid ? _sqids.Encode(aid) : null,
            FailureReason: row.FailureReason,
            DurationMs: row.DurationMs);
    }

    /// <summary>
    /// Emits an audit row for a job lifecycle transition. The
    /// <c>DetailsJson</c> carries the template Sqid + format + status only —
    /// the payload is intentionally PII-free.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>REPORT.JOB.ENQUEUED</c>).</param>
    /// <param name="row">The persisted row, post-mutation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, ReportJob row, CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            templateSqid = _sqids.Encode(row.ReportTemplateId),
            format = ((ExportFormat)row.Format).ToString(),
            status = row.Status.ToString(),
        });
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Information,
            actorId: actor,
            targetEntity: nameof(ReportJob),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
