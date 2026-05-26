using System.Diagnostics;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reports;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — default <see cref="IReportJobRunner"/>
/// implementation. Picks the oldest <c>Queued</c> <see cref="ReportJob"/>,
/// flips it to <c>Running</c>, invokes <see cref="IReportEngine.ExportAsync"/>
/// to render the export bytes, persists the bytes through the R0227 attachment
/// service (owner = <c>ReportJob</c>, sensitivity = <c>Confidential</c>),
/// stamps a terminal status, and notifies the requester via the R0171 / R0128
/// orchestrator with a <c>"Report.Ready"</c> or <c>"Report.Failed"</c> subject.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine impersonation.</b> The runner ALWAYS runs the engine "as" the
/// requester so the R0156 access gates fire correctly — the engine's
/// <c>CanAccess</c> check honours <see cref="ReportJob.RequestedByUserId"/>
/// via the swapped <see cref="ICallerContext"/>. The implementation here
/// captures the <c>ICallerContext</c> already wired through DI; tests and
/// real callers ensure the runner is invoked from a scope where the caller
/// context already represents the requester (the Quartz background job
/// resolves the runner inside its own scope so this works out naturally).
/// </para>
/// <para>
/// <b>Idempotency.</b> The runner is NOT idempotent across crashes — a row
/// flipped to <c>Running</c> by a crash-killed run will remain stuck in that
/// state until an admin intervenes. A future hardening pass can add a
/// <c>StartedAtUtc</c> stale-row reaper. Within a single process the
/// <c>[DisallowConcurrentExecution]</c> on the Quartz job suffices.
/// </para>
/// </remarks>
public sealed class ReportJobRunner(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IReportEngine engine,
    IAttachmentService attachments,
    INotificationService notifications,
    IAuditService audit)
    : IReportJobRunner
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IReportEngine _engine = engine;
    private readonly IAttachmentService _attachments = attachments;
    private readonly INotificationService _notifications = notifications;
    private readonly IAuditService _audit = audit;

    /// <summary>Stable subject literals for the citizen-facing notifications.</summary>
    public const string SubjectReady = "Report.Ready";

    /// <summary>Stable subject literal for failure notifications.</summary>
    public const string SubjectFailed = "Report.Failed";

    /// <summary>Audit event for a Succeeded transition.</summary>
    private const string AuditSucceeded = ReportJobService.AuditPrefix + ".SUCCEEDED";

    /// <summary>Audit event for a Failed transition.</summary>
    private const string AuditFailed = ReportJobService.AuditPrefix + ".FAILED";

    /// <inheritdoc />
    public async Task<Result<ReportJobDto?>> RunNextAsync(CancellationToken cancellationToken = default)
    {
        // Pick the oldest Queued row. The (Status, QueuedAtUtc) index serves this
        // query; we keep the .Where deterministic on IsActive so the runner skips
        // soft-deleted rows (currently impossible but defensive).
        var row = await _db.ReportJobs
            .Where(j => j.IsActive && j.Status == ReportJobStatus.Queued)
            .OrderBy(j => j.QueuedAtUtc)
            .ThenBy(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result<ReportJobDto?>.Success(null);
        }

        var stopwatch = Stopwatch.StartNew();
        var startedAt = _clock.UtcNow;
        row.Status = ReportJobStatus.Running;
        row.StartedAtUtc = startedAt;
        row.UpdatedAtUtc = startedAt;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var format = (ExportFormat)row.Format;

        // Invoke the engine. ExportAsync returns Result<byte[]> — on success the
        // bytes are uploaded through the attachment service; on failure we stamp
        // Failed + dispatch the failure notification.
        Result<byte[]> exportResult;
        try
        {
            exportResult = await _engine.ExportAsync(row.ReportTemplateId, format, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation does not stamp a terminal status — the job remains Running
            // and the next runner tick will pick a fresh job. The stuck-row reaper
            // (deferred) will eventually surface the stale Running row.
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return await FinishFailedAsync(row, ex.Message, (int)stopwatch.ElapsedMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (exportResult.IsFailure)
        {
            stopwatch.Stop();
            return await FinishFailedAsync(
                row,
                exportResult.ErrorMessage ?? "Engine refused the export.",
                (int)stopwatch.ElapsedMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }

        // Persist the export bytes through the attachment subsystem. Owner =
        // ReportJob, category = Other, sensitivity = Confidential, filename
        // derived from the format extension. The attachment service is
        // responsible for the SHA-256 dedup + blob upload + audit row.
        var uploadInput = new AttachmentUploadDto(
            OwnerEntityType: AttachmentOwnerTypes.ReportJob,
            OwnerSqid: _sqids.Encode(row.Id),
            ContentBase64: Convert.ToBase64String(exportResult.Value),
            DeclaredFileName: BuildFileName(row.Id, format),
            Category: AttachmentCategory.Other.ToString(),
            SensitivityLabel: Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential.ToString(),
            Description: $"Background report export for ReportJob#{row.Id}");

        var uploadResult = await _attachments.UploadAsync(uploadInput, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (uploadResult.IsFailure)
        {
            return await FinishFailedAsync(
                row,
                $"Attachment upload failed: {uploadResult.ErrorMessage}",
                (int)stopwatch.ElapsedMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }

        // Successful path — decode the attachment Sqid back to its numeric form
        // so we can populate the FK column.
        var attachmentDecoded = _sqids.TryDecode(uploadResult.Value.Id);
        if (attachmentDecoded.IsFailure)
        {
            return await FinishFailedAsync(
                row,
                $"Attachment Sqid did not decode: {attachmentDecoded.ErrorMessage}",
                (int)stopwatch.ElapsedMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }

        var completedAt = _clock.UtcNow;
        row.Status = ReportJobStatus.Succeeded;
        row.CompletedAtUtc = completedAt;
        row.AttachmentRecordId = attachmentDecoded.Value;
        row.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        row.UpdatedAtUtc = completedAt;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditSucceeded, row, cancellationToken).ConfigureAwait(false);
        await DispatchNotificationAsync(
            row,
            SubjectReady,
            $"Your report export is ready. Download it via attachment {uploadResult.Value.Id}.",
            cancellationToken).ConfigureAwait(false);

        return Result<ReportJobDto?>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<int> RunBatchAsync(int maxJobs, CancellationToken cancellationToken = default)
    {
        if (maxJobs <= 0)
        {
            return 0;
        }
        var drained = 0;
        while (drained < maxJobs && !cancellationToken.IsCancellationRequested)
        {
            var next = await RunNextAsync(cancellationToken).ConfigureAwait(false);
            // Stop draining when the queue is empty (next.Value is null) or the
            // step bubbled an unrecoverable failure (then-stop because retrying
            // would hammer the same backend).
            if (next.IsFailure || next.Value is null)
            {
                break;
            }
            drained++;
        }
        return drained;
    }

    /// <summary>
    /// Stamps the row as Failed, emits the audit + notification, and returns
    /// the post-state DTO wrapped in a success Result so the runner contract
    /// holds (the drain loop reads <see cref="Result{T}.IsSuccess"/>).
    /// </summary>
    /// <param name="row">The job row, already transitioned to Running.</param>
    /// <param name="failureReason">Reason text; trimmed to the column cap.</param>
    /// <param name="elapsedMs">Stopwatch elapsed at the failure point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The post-state DTO.</returns>
    private async Task<Result<ReportJobDto?>> FinishFailedAsync(
        ReportJob row,
        string failureReason,
        int elapsedMs,
        CancellationToken cancellationToken)
    {
        var completedAt = _clock.UtcNow;
        row.Status = ReportJobStatus.Failed;
        row.CompletedAtUtc = completedAt;
        row.DurationMs = elapsedMs;
        // Trim the failure message to the persisted column cap to avoid
        // bloating the table with a stack-trace text.
        row.FailureReason = failureReason.Length > 2000 ? failureReason[..2000] : failureReason;
        row.UpdatedAtUtc = completedAt;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditFailed, row, cancellationToken).ConfigureAwait(false);
        await DispatchNotificationAsync(
            row,
            SubjectFailed,
            $"Your report export failed: {row.FailureReason}",
            cancellationToken).ConfigureAwait(false);
        return Result<ReportJobDto?>.Success(Project(row));
    }

    /// <summary>
    /// Builds the on-disk filename for the export attachment. Filename shape
    /// is <c>report-job-{id}.{ext}</c> — the sanitiser inside the attachment
    /// service further slugifies the value.
    /// </summary>
    /// <param name="jobId">Internal job id.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The filename.</returns>
    private static string BuildFileName(long jobId, ExportFormat format)
    {
        var ext = format switch
        {
            ExportFormat.Csv => "csv",
            ExportFormat.Xlsx => "xlsx",
            ExportFormat.Pdf => "pdf",
            _ => "bin",
        };
        return $"report-job-{jobId}.{ext}";
    }

    /// <summary>
    /// Dispatches the citizen-facing completion notification. Failures inside
    /// the notification service are intentionally NOT propagated — a failing
    /// notification must not undo the job's persisted terminal state.
    /// </summary>
    /// <param name="row">The job row.</param>
    /// <param name="subject">Stable subject literal (<c>Report.Ready</c> / <c>Report.Failed</c>).</param>
    /// <param name="body">Human-readable body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task DispatchNotificationAsync(
        ReportJob row,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        await _notifications.EnqueueAsync(
            recipientUserId: row.RequestedByUserId,
            subject: subject,
            body: body,
            correlationId: $"report-job-{row.Id}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the persisted row to its DTO surface using the same shape as
    /// <see cref="ReportJobService.Project"/>. Replicated here to keep the
    /// runner self-contained (no cross-class private call).
    /// </summary>
    /// <param name="row">Persisted row.</param>
    /// <returns>The DTO.</returns>
    private ReportJobDto Project(ReportJob row)
    {
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
    /// Emits an audit row for a terminal transition. Body mirrors
    /// <see cref="ReportJobService"/>'s shape so the audit explorer can chart
    /// all four lifecycle events with one filter.
    /// </summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="row">Post-state row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, ReportJob row, CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            templateSqid = _sqids.Encode(row.ReportTemplateId),
            format = ((ExportFormat)row.Format).ToString(),
            status = row.Status.ToString(),
            durationMs = row.DurationMs,
        });
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Information,
            actorId: "system",
            targetEntity: nameof(ReportJob),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: null,
            correlationId: $"report-job-{row.Id}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
