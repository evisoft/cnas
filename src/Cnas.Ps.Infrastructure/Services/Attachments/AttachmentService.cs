using System.Security.Cryptography;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — default <see cref="IAttachmentService"/> implementation. Owns
/// the upload / list / archive / delete / download lifecycle for
/// <see cref="AttachmentRecord"/> rows, enforces the per-owner dedup contract, and
/// emits the per-call audit rows mandated by the interface remarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upload pipeline.</b>
/// <list type="number">
///   <item>Decode the base64 payload (validator already short-circuited malformed wire shapes).</item>
///   <item>Call <see cref="IAttachmentValidator.Validate"/> for magic-byte sniffing,
///         extension cross-check, and filename sanitisation.</item>
///   <item>Decode the owner Sqid; reject on failure.</item>
///   <item>Compute the SHA-256 over the decoded bytes.</item>
///   <item>Dedup short-circuit — return the existing row if one already exists with the
///         same (owner type, owner id, sha256) and is active.</item>
///   <item>Store the blob via <see cref="IBlobStorage.PutAsync"/>.</item>
///   <item>Persist the row + emit the <c>ATTACHMENT.UPLOADED</c> Sensitive audit row.</item>
/// </list>
/// </para>
/// <para>
/// <b>PII guard in audit.</b> The audit <c>DetailsJson</c> deliberately omits the
/// filename and the description. Both can carry citizen-supplied free-form text that
/// might inadvertently embed an IDNP or other PII; the audit row therefore captures
/// only structural metadata (owner reference, size, category, sensitivity, sha256).
/// </para>
/// </remarks>
public sealed class AttachmentService(
    ICnasDbContext db,
    IBlobStorage blobs,
    IAttachmentValidator validator,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IAuditService audit,
    ICallerContext caller,
    IOptions<AttachmentOptions> options)
    : IAttachmentService
{
    private readonly ICnasDbContext _db = db;
    private readonly IBlobStorage _blobs = blobs;
    private readonly IAttachmentValidator _validator = validator;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IAuditService _audit = audit;
    private readonly ICallerContext _caller = caller;
    private readonly AttachmentOptions _options = options.Value;

    /// <summary>Role that grants cross-uploader <c>Attachment.ReadAny</c> permission.</summary>
    private const string StaffRole = "cnas-user";

    /// <summary>Stable audit code for successful uploads (Sensitive).</summary>
    private const string AuditUploaded = "ATTACHMENT.UPLOADED";

    /// <summary>Stable audit code for downloads (Sensitive).</summary>
    private const string AuditDownloaded = "ATTACHMENT.DOWNLOADED";

    /// <summary>Stable audit code for soft-archives (Notice).</summary>
    private const string AuditArchived = "ATTACHMENT.ARCHIVED";

    /// <summary>Stable audit code for soft-deletes (Critical).</summary>
    private const string AuditDeleted = "ATTACHMENT.DELETED";

    /// <inheritdoc />
    public async Task<Result<AttachmentRecordDto>> UploadAsync(
        AttachmentUploadDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_caller.UserId is not long uploaderId)
        {
            return Result<AttachmentRecordDto>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        if (!AttachmentOwnerTypes.All.Contains(input.OwnerEntityType, StringComparer.Ordinal))
        {
            return Result<AttachmentRecordDto>.Failure(
                ErrorCodes.ValidationFailed, "Unknown owner entity type.");
        }

        var ownerDecode = _sqids.TryDecode(input.OwnerSqid);
        if (ownerDecode.IsFailure)
        {
            return Result<AttachmentRecordDto>.Failure(ownerDecode.ErrorCode!, ownerDecode.ErrorMessage!);
        }
        var ownerId = ownerDecode.Value;

        if (!Enum.TryParse<AttachmentCategory>(input.Category, ignoreCase: false, out var category))
        {
            return Result<AttachmentRecordDto>.Failure(
                ErrorCodes.ValidationFailed, "Unknown attachment category.");
        }

        var sensitivity = SensitivityLabel.Confidential;
        if (!string.IsNullOrEmpty(input.SensitivityLabel)
            && !Enum.TryParse(input.SensitivityLabel, ignoreCase: false, out sensitivity))
        {
            return Result<AttachmentRecordDto>.Failure(
                ErrorCodes.ValidationFailed, "Unknown sensitivity label.");
        }

        // Decode the base64 payload. The validator already proved it parses; do it again
        // here because the service is also reachable from non-API callers (background jobs).
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(input.ContentBase64);
        }
        catch (FormatException ex)
        {
            return Result<AttachmentRecordDto>.Failure(
                ErrorCodes.ValidationFailed, $"ContentBase64 is not valid base64: {ex.Message}");
        }

        var validation = _validator.Validate(decoded, input.DeclaredFileName);
        if (validation.IsFailure)
        {
            return Result<AttachmentRecordDto>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        var sha = ComputeSha256Hex(decoded);

        // Dedup short-circuit — within the owner, return the existing active row.
        var existing = await _db.AttachmentRecords
            .Where(a => a.OwnerEntityType == input.OwnerEntityType
                     && a.OwnerEntityId == ownerId
                     && a.Sha256Hex == sha
                     && a.IsActive)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<AttachmentRecordDto>.Success(Project(existing));
        }

        var now = _clock.UtcNow;
        var storageKey =
            $"attachments/{now:yyyy}/{now:MM}/{now:dd}/{Guid.NewGuid():N}";

        await _blobs.PutAsync(storageKey, decoded, cancellationToken).ConfigureAwait(false);

        var row = new AttachmentRecord
        {
            OwnerEntityType = input.OwnerEntityType,
            OwnerEntityId = ownerId,
            FileName = validation.Value.SafeFileName,
            ContentType = validation.Value.DetectedContentType,
            SizeBytes = decoded.LongLength,
            StorageKey = storageKey,
            Sha256Hex = sha,
            Category = category,
            SensitivityLevel = (int)sensitivity,
            Description = input.Description,
            UploadedByUserId = uploaderId,
            UploadedUtc = now,
            IsArchived = false,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.AttachmentRecords.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditUploaded,
            AuditSeverity.Sensitive,
            row,
            extraJsonObject: new
            {
                ownerEntityType = input.OwnerEntityType,
                ownerEntitySqid = _sqids.Encode(ownerId),
                sizeBytes = row.SizeBytes,
                category = category.ToString(),
                sensitivityLabel = sensitivity.ToString(),
                sha256 = sha,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<AttachmentRecordDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<AttachmentDownloadDto>> DownloadAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadActiveAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.failure is { } failure)
        {
            return Result<AttachmentDownloadDto>.Failure(failure.code, failure.message);
        }
        var row = loaded.row!;

        if (!HasAccess(row))
        {
            return Result<AttachmentDownloadDto>.Failure(
                ErrorCodes.Forbidden, "Not your attachment.");
        }

        byte[] bytes;
        try
        {
            bytes = await _blobs.GetAsync(row.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Result<AttachmentDownloadDto>.Failure(
                ErrorCodes.FileUnavailable, "Attachment blob is missing.");
        }

        await EmitAuditAsync(
            AuditDownloaded,
            AuditSeverity.Sensitive,
            row,
            extraJsonObject: new
            {
                ownerEntityType = row.OwnerEntityType,
                ownerEntitySqid = _sqids.Encode(row.OwnerEntityId),
                sizeBytes = row.SizeBytes,
                sha256 = row.Sha256Hex,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<AttachmentDownloadDto>.Success(new AttachmentDownloadDto(
            bytes, row.ContentType, row.FileName));
    }

    /// <inheritdoc />
    public async Task<Result<AttachmentRecordDto>> GetAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadActiveAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.failure is { } failure)
        {
            return Result<AttachmentRecordDto>.Failure(failure.code, failure.message);
        }
        var row = loaded.row!;
        if (!HasAccess(row))
        {
            return Result<AttachmentRecordDto>.Failure(ErrorCodes.Forbidden, "Not your attachment.");
        }
        return Result<AttachmentRecordDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AttachmentRecordDto>>> ListAsync(
        string ownerEntityType,
        string ownerSqid,
        CancellationToken cancellationToken = default)
    {
        if (!AttachmentOwnerTypes.All.Contains(ownerEntityType, StringComparer.Ordinal))
        {
            return Result<IReadOnlyList<AttachmentRecordDto>>.Failure(
                ErrorCodes.ValidationFailed, "Unknown owner entity type.");
        }

        var decode = _sqids.TryDecode(ownerSqid);
        if (decode.IsFailure)
        {
            return Result<IReadOnlyList<AttachmentRecordDto>>.Failure(
                decode.ErrorCode!, decode.ErrorMessage!);
        }
        var ownerId = decode.Value;

        var rows = await _db.AttachmentRecords
            .Where(a => a.OwnerEntityType == ownerEntityType
                     && a.OwnerEntityId == ownerId
                     && a.IsActive
                     && !a.IsArchived)
            .OrderBy(a => a.UploadedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dtos = rows.Select(Project).ToList();
        return Result<IReadOnlyList<AttachmentRecordDto>>.Success(dtos);
    }

    /// <inheritdoc />
    public async Task<Result> ArchiveAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadActiveAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.failure is { } failure)
        {
            return Result.Failure(failure.code, failure.message);
        }
        var row = loaded.row!;
        if (!HasAccess(row))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Not your attachment.");
        }

        row.IsArchived = true;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditArchived,
            AuditSeverity.Notice,
            row,
            extraJsonObject: new
            {
                ownerEntityType = row.OwnerEntityType,
                ownerEntitySqid = _sqids.Encode(row.OwnerEntityId),
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadActiveAsync(attachmentSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.failure is { } failure)
        {
            return Result.Failure(failure.code, failure.message);
        }
        var row = loaded.row!;
        if (!HasAccess(row))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Not your attachment.");
        }

        row.IsActive = false;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditDeleted,
            AuditSeverity.Critical,
            row,
            extraJsonObject: new
            {
                ownerEntityType = row.OwnerEntityType,
                ownerEntitySqid = _sqids.Encode(row.OwnerEntityId),
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Decodes the supplied Sqid, loads the corresponding active row, and returns
    /// either a typed failure tuple OR the loaded entity. Soft-deleted rows surface
    /// as NotFound; soft-archived rows are still loadable.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Loaded row or stable failure tuple.</returns>
    private async Task<(AttachmentRecord? row, (string code, string message)? failure)>
        LoadActiveAsync(string attachmentSqid, CancellationToken cancellationToken)
    {
        var decode = _sqids.TryDecode(attachmentSqid);
        if (decode.IsFailure)
        {
            return (null, (decode.ErrorCode!, decode.ErrorMessage!));
        }

        var row = await _db.AttachmentRecords
            .SingleOrDefaultAsync(a => a.Id == decode.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return (null, (ErrorCodes.NotFound, "Attachment not found."));
        }
        if (_caller.UserId is null)
        {
            return (null, (ErrorCodes.Unauthorized, "Not authenticated."));
        }
        return (row, null);
    }

    /// <summary>
    /// Returns <c>true</c> when the caller is the uploader OR holds the staff role
    /// granting <c>Attachment.ReadAny</c>.
    /// </summary>
    /// <param name="row">Loaded attachment row.</param>
    /// <returns><c>true</c> when access is permitted.</returns>
    private bool HasAccess(AttachmentRecord row)
    {
        if (_caller.UserId is not long callerId)
        {
            return false;
        }
        if (row.UploadedByUserId == callerId)
        {
            return true;
        }
        return _caller.Roles.Contains(StaffRole, StringComparer.Ordinal);
    }

    /// <summary>
    /// Projects an entity row to its public DTO with Sqid-encoded identifiers and
    /// stable enum string names. Centralised so encoding rules apply identically.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The output DTO.</returns>
    private AttachmentRecordDto Project(AttachmentRecord row)
    {
        var label = (SensitivityLabel)row.SensitivityLevel;
        return new AttachmentRecordDto(
            Id: _sqids.Encode(row.Id),
            OwnerEntityType: row.OwnerEntityType,
            OwnerSqid: _sqids.Encode(row.OwnerEntityId),
            FileName: row.FileName,
            ContentType: row.ContentType,
            SizeBytes: row.SizeBytes,
            Sha256Hex: row.Sha256Hex,
            Category: row.Category.ToString(),
            SensitivityLabel: label.ToString(),
            Description: row.Description,
            UploadedByUserSqid: _sqids.Encode(row.UploadedByUserId),
            UploadedUtc: row.UploadedUtc,
            IsArchived: row.IsArchived);
    }

    /// <summary>
    /// Serialises <paramref name="extraJsonObject"/> to JSON and emits an audit row
    /// under <paramref name="eventCode"/> at <paramref name="severity"/>. The row's
    /// id is always populated as the audit target id; <paramref name="extraJsonObject"/>
    /// must NEVER include PII (filename, description) — see class-level remarks.
    /// </summary>
    /// <param name="eventCode">Stable audit code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="row">Attachment row.</param>
    /// <param name="extraJsonObject">Structured details payload (PII-free).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        AttachmentRecord row,
        object extraJsonObject,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(extraJsonObject);
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: severity,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(AttachmentRecord),
            targetEntityId: row.Id,
            detailsJson: json,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of <paramref name="bytes"/>. Static helper
    /// kept private so the digest convention stays in one place.
    /// </summary>
    /// <param name="bytes">Payload to hash.</param>
    /// <returns>Lowercase hex digest.</returns>
    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
