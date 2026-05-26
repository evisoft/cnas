namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0227 / TOR UI 014 — server-side primitive backing the reusable file-attachment
/// widget. Each row pins ONE binary attachment to an OWNER (a polymorphic reference —
/// any entity in the system can carry attachments by stringifying its CLR name plus
/// raw <see cref="long"/> id) and carries the metadata that the future Blazor widget
/// surfaces to the citizen / examiner (filename, MIME, size, category, sensitivity,
/// optional description). The binary itself lives in the configured blob backend
/// (R0227 ships <c>LocalDiskBlobStorage</c>; production MinIO adapter is reused via
/// <c>IFileStorage</c>); only the opaque <see cref="StorageKey"/> is persisted here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polymorphic ownership.</b> The pair
/// (<see cref="OwnerEntityType"/>, <see cref="OwnerEntityId"/>) identifies the
/// row that owns this attachment. No database-level foreign key is declared because the
/// owner type varies — the application layer validates the owner-type string against the
/// frozen allow-list constants in
/// <c>Cnas.Ps.Application.Attachments.AttachmentOwnerTypes</c>. Storing the type as a
/// stable string keeps the table append-only across owner-table renames / refactors.
/// </para>
/// <para>
/// <b>Dedup contract.</b> Within an owner, two uploads of the BYTE-IDENTICAL file
/// produce ONE row, not two — the service short-circuits when a row already exists with
/// the same <see cref="Sha256Hex"/> under the same owner. The filtered unique index in
/// <c>AttachmentRecordConfiguration</c>
/// (<c>(OwnerEntityType, OwnerEntityId, Sha256Hex) WHERE IsActive=true</c>) is the
/// DB-side safety net against a racing concurrent insert.
/// </para>
/// <para>
/// <b>Sensitivity label.</b> Each attachment carries its own sensitivity ordinal in
/// <see cref="SensitivityLevel"/> — the numeric value mirrors
/// <c>Cnas.Ps.Contracts.Security.SensitivityLabel</c> (Public=0, Internal=1,
/// Confidential=2, Restricted=3); we deliberately use the raw <see cref="int"/> in the
/// domain layer because Core cannot reference Contracts without violating the
/// architecture rule. The DTO surface re-projects the value as the typed enum and the
/// service layer round-trips it. Default 2 (Confidential) because the typical attachment
/// carries citizen PII.
/// </para>
/// <para>
/// <b>Archive vs delete.</b> <see cref="IsArchived"/> is a soft-archive flag distinct
/// from the inherited <see cref="AuditableEntity.IsActive"/> soft-delete flag. Archived
/// attachments are still discoverable in audit / forensic queries (and counted in
/// per-owner reports) but are hidden from the default citizen-facing list — useful for
/// historical attachments that should no longer surface but must not be erased. A
/// soft-deleted (<c>IsActive=false</c>) attachment is hidden everywhere.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The <see cref="AuditableEntity.Id"/> primary key is exposed on
/// the <c>AttachmentRecordDto</c> as a Sqid string per CLAUDE.md RULE 3 — hence the
/// <see cref="IExternalId"/> marker.
/// </para>
/// </remarks>
public sealed class AttachmentRecord : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable CLR type name of the owning entity (e.g. <c>"ServiceApplication"</c>,
    /// <c>"WorkflowTask"</c>, <c>"UserProfile"</c>). Validated against the allow-list
    /// <c>AttachmentOwnerTypes</c> before persistence. Storing the type as a string
    /// (rather than a numeric enum) keeps the persistence contract resilient to owner
    /// renames and lets new owner types be added without a migration.
    /// </summary>
    public required string OwnerEntityType { get; set; }

    /// <summary>
    /// Raw <see cref="long"/> primary key of the owning entity. No FK constraint is
    /// declared because <see cref="OwnerEntityType"/> can change which table the id
    /// resolves to.
    /// </summary>
    public long OwnerEntityId { get; set; }

    /// <summary>
    /// Original filename as supplied by the uploader, sanitised by
    /// <c>IAttachmentValidator</c> — path separators stripped, lowercased / slugified,
    /// extension preserved. Surfaced to the user in the attachment list and on the
    /// download endpoint's <c>Content-Disposition</c> header.
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// MIME type DETECTED from the magic-byte sniff (not the value the client claimed).
    /// One of the configured allow-list types in <c>AttachmentOptions.AllowedMimeTypes</c>.
    /// </summary>
    public required string ContentType { get; set; }

    /// <summary>Size in bytes of the persisted blob. Capped at <c>AttachmentOptions.MaxBytes</c>.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Opaque key returned by the blob backend's <c>PutAsync</c> — used to fetch the
    /// blob back on download. The format is backend-specific (e.g.
    /// <c>attachments/yyyy/MM/dd/{guid}</c> for the local disk adapter); callers MUST
    /// treat it as opaque and never construct or parse it.
    /// </summary>
    public required string StorageKey { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 digest of the uploaded bytes. Drives the dedup short-circuit
    /// (a second upload of the byte-identical file under the same owner returns the
    /// existing row) and serves as the integrity-verification anchor on download.
    /// </summary>
    public required string Sha256Hex { get; set; }

    /// <summary>
    /// Semantic category — see <see cref="AttachmentCategory"/>. Independent of the MIME
    /// type; a PDF can be either an <see cref="AttachmentCategory.Income"/> proof or a
    /// <see cref="AttachmentCategory.Medical"/> certificate.
    /// </summary>
    public AttachmentCategory Category { get; set; }

    /// <summary>
    /// Per-attachment sensitivity ordinal mirroring
    /// <c>Cnas.Ps.Contracts.Security.SensitivityLabel</c> — values are
    /// <c>Public=0</c>, <c>Internal=1</c>, <c>Confidential=2</c>, <c>Restricted=3</c>.
    /// Stored as <see cref="int"/> because the Core layer cannot reference Contracts
    /// without violating the architecture rule; the cref above is intentionally a string.
    /// Default 2 (Confidential) because the typical attachment carries citizen PII.
    /// </summary>
    public int SensitivityLevel { get; set; } = 2;

    /// <summary>
    /// Optional uploader-supplied description. ≤ 500 chars (enforced by the input
    /// validator); <see langword="null"/> when not supplied. NEVER include PII here —
    /// the field is shown verbatim in attachment lists.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> primary id of the uploader.</summary>
    public long UploadedByUserId { get; set; }

    /// <summary>UTC instant when the attachment was uploaded — distinct from
    /// <see cref="AuditableEntity.CreatedAtUtc"/> so the upload timestamp survives a
    /// row-level re-bake.</summary>
    public DateTime UploadedUtc { get; set; }

    /// <summary>
    /// Soft-archive flag — <see langword="true"/> means the attachment is hidden from
    /// the default citizen-facing list but remains discoverable via audit / forensic
    /// queries. Distinct from <see cref="AuditableEntity.IsActive"/> (soft-delete).
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool IsArchived { get; set; }
}
