using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — server-side surface backing the reusable file-attachment
/// widget. Drives the upload / list / archive / delete / download lifecycle for
/// <c>AttachmentRecord</c> rows. The interface is the seam every owning controller
/// calls through — the future Blazor widget will go through here as well.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every method enforces an authenticated-caller gate; the
/// download / archive / delete paths additionally enforce that the caller is either
/// the uploader OR holds the cross-attachment <c>Attachment.ReadAny</c> permission
/// (currently approximated by the <c>cnas-user</c> role until R0035 / SEC 016
/// rebakes per-permission policies). Anonymous callers receive
/// <see cref="ErrorCodes.Unauthorized"/>; foreign callers receive
/// <see cref="ErrorCodes.Forbidden"/>.
/// </para>
/// <para>
/// <b>Audit shape.</b> Successful uploads emit
/// <c>ATTACHMENT.UPLOADED</c> at <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Sensitive"/>
/// (PII surface); downloads emit <c>ATTACHMENT.DOWNLOADED</c> at the same severity;
/// archives emit <c>ATTACHMENT.ARCHIVED</c> at
/// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Notice"/>; deletes emit
/// <c>ATTACHMENT.DELETED</c> at
/// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Critical"/>. The audit
/// <c>DetailsJson</c> carries the owner reference, size, category, and sensitivity
/// label but NEVER the filename (PII guard — a citizen's IDNP inadvertently embedded
/// in the filename cannot leak through the audit trail).
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every id surface is the Sqid string form per CLAUDE.md
/// RULE 3. Raw <see cref="long"/> primary keys never appear here.
/// </para>
/// </remarks>
public interface IAttachmentService
{
    /// <summary>
    /// Validates and persists a new attachment. Performs magic-byte sniffing,
    /// size-cap enforcement, extension cross-check, SHA-256 hashing, per-owner
    /// dedup short-circuit, blob upload, audit emission.
    /// </summary>
    /// <param name="input">Upload payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the persisted (or dedup-returned) attachment row.
    /// <see cref="ErrorCodes.Unauthorized"/> when anonymous;
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad payload shape;
    /// <see cref="ErrorCodes.FileTooLarge"/> when the decoded payload exceeds
    /// <c>AttachmentOptions.MaxBytes</c>;
    /// <see cref="ErrorCodes.FileTypeMismatch"/> on magic-byte / extension mismatch;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the owner Sqid does not decode.
    /// </returns>
    Task<Result<AttachmentRecordDto>> UploadAsync(
        AttachmentUploadDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the attachment bytes back to the caller. Enforces the per-call audit
    /// emission and the uploader-OR-staff access gate.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the bytes + content type + sanitised filename.
    /// <see cref="ErrorCodes.NotFound"/> when the row is missing or soft-deleted;
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is neither uploader nor
    /// staff; <see cref="ErrorCodes.InvalidSqid"/> on bad id;
    /// <see cref="ErrorCodes.FileUnavailable"/> when the row exists but the blob
    /// backend has lost the object.
    /// </returns>
    Task<Result<AttachmentDownloadDto>> DownloadAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every visible attachment for the supplied owner — excludes
    /// soft-archived (<c>IsArchived=true</c>) and soft-deleted (<c>IsActive=false</c>)
    /// rows by default.
    /// </summary>
    /// <param name="ownerEntityType">Stable CLR-type name of the owning entity.</param>
    /// <param name="ownerSqid">Sqid-encoded id of the owning entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the list (oldest first by <c>UploadedUtc</c>);
    /// <see cref="ErrorCodes.InvalidSqid"/> on bad id;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the owner type is not in the
    /// frozen allow-list.
    /// </returns>
    Task<Result<IReadOnlyList<AttachmentRecordDto>>> ListAsync(
        string ownerEntityType,
        string ownerSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single attachment's metadata WITHOUT the byte payload. Enforces the
    /// same uploader-OR-staff access gate as the download path; does not emit an
    /// audit row (the per-call audit fires on download).
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the metadata DTO; stable failure codes per <see cref="DownloadAsync"/>
    /// (NotFound / Forbidden / Unauthorized / InvalidSqid).
    /// </returns>
    Task<Result<AttachmentRecordDto>> GetAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-archives the attachment — sets <c>IsArchived=true</c> but leaves
    /// <c>IsActive</c> unchanged. The row remains discoverable to audit / forensic
    /// queries; the default list path hides it.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a stable failure code (NotFound / Forbidden / InvalidSqid).</returns>
    Task<Result> ArchiveAsync(string attachmentSqid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the attachment — sets <c>IsActive=false</c>. Emits a
    /// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Critical"/> audit row. The blob
    /// is left in place (cleanup is handled by a separate retention job).
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded attachment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a stable failure code (NotFound / Forbidden / InvalidSqid).</returns>
    Task<Result> DeleteAsync(string attachmentSqid, CancellationToken cancellationToken = default);
}
