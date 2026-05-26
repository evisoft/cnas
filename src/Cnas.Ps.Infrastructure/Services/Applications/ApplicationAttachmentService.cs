using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Applications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Applications;

/// <summary>
/// R0322 / TOR UI 014 — concrete implementation of
/// <see cref="IApplicationAttachmentService"/>. Owns the
/// <see cref="ApplicationAttachment"/> entity lifecycle (attach / remove /
/// record-scan / list / get). Emits Notice-severity audit rows on every mutation
/// and bumps the <c>CnasMeter</c> counters. PII never appears in audit details —
/// only Sqid ids and enum names.
/// </summary>
public sealed class ApplicationAttachmentService : IApplicationAttachmentService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Stable failure message returned when a duplicate active link exists.</summary>
    public const string DuplicateLinkMessage = "ATTACHMENT_ALREADY_LINKED";

    /// <summary>Stable failure message returned when the row is already soft-removed.</summary>
    public const string AlreadyRemovedMessage = "ATTACHMENT_ALREADY_REMOVED";

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<ApplicationAttachInputDto> _attachValidator;
    private readonly IValidator<ApplicationAttachmentReasonInputDto> _reasonValidator;
    private readonly IValidator<ApplicationAttachmentScanResultInputDto> _scanValidator;
    private readonly IValidator<ApplicationAttachmentFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context for the list path.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="attachValidator">Validator for the attach payload.</param>
    /// <param name="reasonValidator">Validator for the remove-reason payload.</param>
    /// <param name="scanValidator">Validator for the virus-scan-result payload.</param>
    /// <param name="filterValidator">Validator for the list filter.</param>
    public ApplicationAttachmentService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<ApplicationAttachInputDto> attachValidator,
        IValidator<ApplicationAttachmentReasonInputDto> reasonValidator,
        IValidator<ApplicationAttachmentScanResultInputDto> scanValidator,
        IValidator<ApplicationAttachmentFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(attachValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(scanValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _attachValidator = attachValidator;
        _reasonValidator = reasonValidator;
        _scanValidator = scanValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationAttachmentDto>> AttachAsync(
        string applicationSqid,
        ApplicationAttachInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _attachValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ApplicationAttachmentDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var appDecoded = _sqids.TryDecode(applicationSqid);
        if (appDecoded.IsFailure)
        {
            return Result<ApplicationAttachmentDto>.Failure(appDecoded.ErrorCode!, appDecoded.ErrorMessage!);
        }
        var docDecoded = _sqids.TryDecode(input.DocumentSqid);
        if (docDecoded.IsFailure)
        {
            return Result<ApplicationAttachmentDto>.Failure(docDecoded.ErrorCode!, docDecoded.ErrorMessage!);
        }
        var applicationId = appDecoded.Value;
        var documentId = docDecoded.Value;

        var appExists = await _db.Applications
            .AnyAsync(a => a.Id == applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!appExists)
        {
            return Result<ApplicationAttachmentDto>.Failure(ErrorCodes.NotFound, "Application not found.");
        }
        var docExists = await _db.Documents
            .AnyAsync(d => d.Id == documentId, cancellationToken)
            .ConfigureAwait(false);
        if (!docExists)
        {
            return Result<ApplicationAttachmentDto>.Failure(ErrorCodes.NotFound, "Document not found.");
        }

        // Conflict: already an active link for (application, document).
        var duplicate = await _db.ApplicationAttachments
            .AnyAsync(a => a.ApplicationId == applicationId
                && a.DocumentId == documentId
                && a.RemovedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<ApplicationAttachmentDto>.Failure(ErrorCodes.Conflict, DuplicateLinkMessage);
        }

        // The validator confirmed the category parses; we re-parse here.
        var category = Enum.Parse<ApplicationAttachmentCategory>(input.Category, ignoreCase: true);

        var now = _clock.UtcNow;
        var row = new ApplicationAttachment
        {
            ApplicationId = applicationId,
            DocumentId = documentId,
            Category = category,
            IsMandatorySnapshot = input.IsMandatorySnapshot,
            AttachedByUserId = _caller.UserId ?? 0L,
            AttachedAtUtc = now,
            VirusScanStatus = AttachmentVirusScanStatus.Pending,
            Notes = input.Notes,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ApplicationAttachments.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(IApplicationAttachmentService.AuditAttached, row.Id, new
        {
            applicationSqid,
            documentSqid = input.DocumentSqid,
            category = category.ToString(),
            isMandatory = input.IsMandatorySnapshot,
        }, cancellationToken).ConfigureAwait(false);

        CnasMeter.ApplicationAttachmentAttached.Add(1,
            new KeyValuePair<string, object?>("category", category.ToString()));

        return Result<ApplicationAttachmentDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(
        string attachmentSqid,
        ApplicationAttachmentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(attachmentSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var attachmentId = decoded.Value;

        var row = await _db.ApplicationAttachments
            .SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Attachment not found.");
        }
        if (row.RemovedAtUtc is not null)
        {
            return Result.Failure(ErrorCodes.Conflict, AlreadyRemovedMessage);
        }

        var now = _clock.UtcNow;
        row.RemovedAtUtc = now;
        row.RemovedByUserId = _caller.UserId;
        row.RemovalReason = input.Reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(IApplicationAttachmentService.AuditRemoved, row.Id, new
        {
            attachmentSqid,
            applicationSqid = _sqids.Encode(row.ApplicationId),
            documentSqid = _sqids.Encode(row.DocumentId),
            reason = input.Reason,
        }, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RecordVirusScanResultAsync(
        string attachmentSqid,
        ApplicationAttachmentScanResultInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _scanValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(attachmentSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var attachmentId = decoded.Value;

        var row = await _db.ApplicationAttachments
            .SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Attachment not found.");
        }

        var status = Enum.Parse<AttachmentVirusScanStatus>(input.Status, ignoreCase: true);

        var now = _clock.UtcNow;
        row.VirusScanStatus = status;
        row.VirusScannedAtUtc = now;
        row.VirusScannerName = input.ScannerName;
        if (!string.IsNullOrEmpty(input.Notes))
        {
            row.Notes = input.Notes;
        }
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(IApplicationAttachmentService.AuditVirusScanRecorded, row.Id, new
        {
            attachmentSqid,
            status = status.ToString(),
            scanner = input.ScannerName,
        }, cancellationToken).ConfigureAwait(false);

        CnasMeter.ApplicationAttachmentVirusScanCompleted.Add(1,
            new KeyValuePair<string, object?>("status", status.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationAttachmentPageDto>> ListByApplicationAsync(
        string applicationSqid,
        ApplicationAttachmentFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ApplicationAttachmentPageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(applicationSqid);
        if (decoded.IsFailure)
        {
            return Result<ApplicationAttachmentPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var applicationId = decoded.Value;

        var appExists = await _read.Applications
            .AnyAsync(a => a.Id == applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!appExists)
        {
            return Result<ApplicationAttachmentPageDto>.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        IQueryable<ApplicationAttachment> q = _read.ApplicationAttachments
            .Where(a => a.ApplicationId == applicationId);

        if (!filter.IncludeRemoved)
        {
            q = q.Where(a => a.RemovedAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category)
            && Enum.TryParse<ApplicationAttachmentCategory>(filter.Category, ignoreCase: true, out var cat))
        {
            q = q.Where(a => a.Category == cat);
        }

        if (!string.IsNullOrWhiteSpace(filter.VirusScanStatus)
            && Enum.TryParse<AttachmentVirusScanStatus>(filter.VirusScanStatus, ignoreCase: true, out var st))
        {
            q = q.Where(a => a.VirusScanStatus == st);
        }

        var total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(a => a.AttachedAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip(Math.Max(0, filter.Skip))
            .Take(Math.Clamp(filter.Take <= 0 ? 20 : filter.Take, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<ApplicationAttachmentDto>(rows.Count);
        foreach (var r in rows)
        {
            items.Add(ToDto(r));
        }

        return Result<ApplicationAttachmentPageDto>.Success(
            new ApplicationAttachmentPageDto(items, total, filter.Skip, filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationAttachmentDto>> GetByIdAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(attachmentSqid);
        if (decoded.IsFailure)
        {
            return Result<ApplicationAttachmentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var attachmentId = decoded.Value;

        var row = await _read.ApplicationAttachments
            .SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ApplicationAttachmentDto>.Failure(ErrorCodes.NotFound, "Attachment not found.");
        }
        return Result<ApplicationAttachmentDto>.Success(ToDto(row));
    }

    /// <summary>Emits one Notice-severity audit row with the supplied details payload.</summary>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="targetId">Surrogate id of the affected row.</param>
    /// <param name="details">Anonymous payload object — serialised as JSON.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    private async Task RecordAuditAsync(
        string eventCode,
        long targetId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(ApplicationAttachment),
            targetId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ApplicationAttachmentDto ToDto(ApplicationAttachment r) => new(
        Id: _sqids.Encode(r.Id),
        ApplicationSqid: _sqids.Encode(r.ApplicationId),
        DocumentSqid: _sqids.Encode(r.DocumentId),
        Category: r.Category.ToString(),
        IsMandatorySnapshot: r.IsMandatorySnapshot,
        AttachedByUserSqid: _sqids.Encode(r.AttachedByUserId),
        AttachedAtUtc: r.AttachedAtUtc,
        VirusScanStatus: r.VirusScanStatus.ToString(),
        VirusScannedAtUtc: r.VirusScannedAtUtc,
        VirusScannerName: r.VirusScannerName,
        Notes: r.Notes,
        RemovedAtUtc: r.RemovedAtUtc,
        RemovedByUserSqid: r.RemovedByUserId is null ? null : _sqids.Encode(r.RemovedByUserId.Value),
        RemovalReason: r.RemovalReason);
}
