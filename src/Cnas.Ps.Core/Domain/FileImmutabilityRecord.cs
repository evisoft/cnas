using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0137 — application-level marker that the addressed object in the storage backend
/// is to be treated as immutable. Each row is a one-shot stamp; the row's presence
/// (plus <see cref="AuditableEntity.IsActive"/> being true) means "this object MUST
/// NOT be deleted by any callable surface in the platform".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why application-level.</b> The MinIO server supports bucket-level object-lock
/// (S3 Object Lock semantics) but enabling it requires a deployment-time toggle on
/// the bucket itself plus the server cluster running in versioned mode. Until that
/// infrastructure choice is finalised across all environments, the application keeps
/// its own immutability ledger so production calls that ought to refuse a delete on
/// an "archived" object do so deterministically regardless of the MinIO bucket
/// configuration. When the server-side toggle lands the application-level guard
/// becomes belt-and-braces; both layers refuse a delete and the audit log records the
/// rejection in the same way.
/// </para>
/// <para>
/// <b>Row semantics.</b> One row per (bucket, objectKey) pair. The unique index on
/// (Bucket, ObjectKey) covering only <see cref="AuditableEntity.IsActive"/>=true rows
/// makes <c>MarkImmutableAsync</c> idempotent — a second mark for the same key short-
/// circuits without raising a duplicate. The row remembers WHO marked it and WHEN so
/// the audit log can trace the immutability stamp back to a specific actor.
/// </para>
/// <para>
/// <b>Not an external entity.</b> The record is purely an internal implementation
/// detail of the file-storage guard — it never surfaces in any output DTO, REST
/// route, or webhook payload. Marking it as <see cref="IExternalId"/> would falsely
/// imply its surrogate id is part of the public contract, which it is not.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "FILE_IMMUTABILITY")]
public sealed class FileImmutabilityRecord : AuditableEntity
{
    /// <summary>Storage bucket name the immutability stamp applies to.</summary>
    public required string Bucket { get; set; }

    /// <summary>
    /// Object key within <see cref="Bucket"/>. Combined with <see cref="Bucket"/> this
    /// is the natural key under the partial unique index over IsActive=true rows.
    /// </summary>
    public required string ObjectKey { get; set; }

    /// <summary>UTC instant at which the immutability stamp was applied.</summary>
    public DateTime MarkedAtUtc { get; set; }

    /// <summary>
    /// User id of the principal who applied the stamp. <c>null</c> when the stamp
    /// was applied by a system actor (background job, automation) — see the audit
    /// log for the system-source trail in that case.
    /// </summary>
    public long? MarkedByUserId { get; set; }

    /// <summary>
    /// Optional free-form reason captured at marking time. Useful for forensic
    /// debugging ("archived per retention policy 7Y", "tax decision, retention
    /// indefinite"). Capped at 256 chars at the EF mapping layer.
    /// </summary>
    public string? Reason { get; set; }
}
