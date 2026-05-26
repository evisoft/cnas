using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Applications;

/// <summary>
/// R0322 / TOR UI 014 — first-class application-attachment service. Wraps the
/// <c>ApplicationAttachment</c> entity (the rich per-link metadata record) and
/// keeps the legacy denormalised <c>ServiceApplication.AttachmentDocumentIds</c>
/// cache in sync as a side effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metric emission.</b> Every successful mutation writes a
/// Notice-severity audit row with a code drawn from the constants below and
/// increments the matching <c>cnas.application_attachment.*</c> counter on
/// <c>CnasMeter</c>. PII never appears in audit details — only Sqid ids and
/// enum names.
/// </para>
/// <para>
/// <b>Authorisation.</b> The controller layer is responsible for gating each
/// route by policy (citizen-owner or examiner / tech-admin); the service layer
/// asserts the application + document exist and otherwise trusts the caller.
/// </para>
/// </remarks>
public interface IApplicationAttachmentService
{
    /// <summary>Audit event code emitted on a successful attach.</summary>
    public const string AuditAttached = "APPLICATION_ATTACHMENT.ATTACHED";

    /// <summary>Audit event code emitted on a successful soft-remove.</summary>
    public const string AuditRemoved = "APPLICATION_ATTACHMENT.REMOVED";

    /// <summary>Audit event code emitted on a virus-scan-result post.</summary>
    public const string AuditVirusScanRecorded = "APPLICATION_ATTACHMENT.VIRUS_SCAN_RECORDED";

    /// <summary>
    /// Creates a new attachment-link row between an application and an existing
    /// document. Rejects when the same (<c>ApplicationId</c>, <c>DocumentId</c>)
    /// pair is already linked with <c>RemovedAtUtc IS NULL</c> (conflict, the
    /// composite unique index would fail otherwise).
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the parent application.</param>
    /// <param name="input">Attach payload (document id, category, mandatory snapshot, notes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the persisted attachment row.
    /// <see cref="ErrorCodes.InvalidSqid"/> when an Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when the application or document is missing;
    /// <see cref="ErrorCodes.Conflict"/> when a current link already exists;
    /// <see cref="ErrorCodes.ValidationFailed"/> on validator failure.
    /// </returns>
    Task<Result<ApplicationAttachmentDto>> AttachAsync(
        string applicationSqid,
        ApplicationAttachInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-removes an attachment link: stamps <c>RemovedAtUtc</c>,
    /// <c>RemovedByUserId</c>, and <c>RemovalReason</c>. The underlying document
    /// is NOT erased — the citizen can re-attach the same document later.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when no row matches;
    /// <see cref="ErrorCodes.Conflict"/> when the row is already removed;
    /// <see cref="ErrorCodes.ValidationFailed"/> on validator failure.
    /// </returns>
    Task<Result> RemoveAsync(
        string attachmentSqid,
        ApplicationAttachmentReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of a virus scan: flips <c>VirusScanStatus</c> to the
    /// supplied terminal value, stamps <c>VirusScannedAtUtc</c> and
    /// <c>VirusScannerName</c>. Idempotent for repeated identical results.
    /// </summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="input">Scan-result payload (status, scanner name, optional notes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when no row matches;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the status is not one of the
    /// terminal values.
    /// </returns>
    Task<Result> RecordVirusScanResultAsync(
        string attachmentSqid,
        ApplicationAttachmentScanResultInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Paged listing of attachment-link rows for one application, ordered
    /// <c>AttachedAtUtc DESC</c>. Removed rows are excluded by default; pass
    /// <see cref="ApplicationAttachmentFilterDto.IncludeRemoved"/>=true to include
    /// them.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the parent application.</param>
    /// <param name="filter">Optional category / status filter + pagination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the page;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when the application does not exist;
    /// <see cref="ErrorCodes.ValidationFailed"/> on filter validator failure.
    /// </returns>
    Task<Result<ApplicationAttachmentPageDto>> ListByApplicationAsync(
        string applicationSqid,
        ApplicationAttachmentFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Loads one attachment-link row by Sqid id.</summary>
    /// <param name="attachmentSqid">Sqid-encoded id of the attachment row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the row;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when no row matches.
    /// </returns>
    Task<Result<ApplicationAttachmentDto>> GetByIdAsync(
        string attachmentSqid,
        CancellationToken cancellationToken = default);
}
