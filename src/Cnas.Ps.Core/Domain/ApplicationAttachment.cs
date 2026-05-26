namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0322 / TOR UI 014 — first-class application-attachment record. Each row binds
/// one (<see cref="ServiceApplication"/>, <see cref="Document"/>) pair and carries
/// the rich metadata the existing
/// <c>ServiceApplication.AttachmentDocumentIds</c> denormalised list cannot
/// express: semantic category, mandatory-snapshot flag, attached-by attribution,
/// virus-scan lifecycle, optional removal record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> Going forward this entity is the canonical
/// per-attachment metadata record; the denormalised
/// <c>AttachmentDocumentIds</c> list on <see cref="ServiceApplication"/> stays
/// as a cheap join-free index for backwards compatibility. New reader code
/// SHOULD prefer this entity for richer projections.
/// </para>
/// <para>
/// <b>Mandatory snapshot.</b> <see cref="IsMandatorySnapshot"/> captures
/// whether the service definition (the active service-passport version at attach
/// time) flagged this document type as mandatory. The snapshot is preserved
/// independently of the passport so a later republish of the passport (which
/// can change a document from mandatory to optional or vice-versa) does not
/// rewrite the per-attachment expectation. See CLAUDE.md cross-cutting
/// "Immutable Snapshots" for the rationale.
/// </para>
/// <para>
/// <b>Soft-delete contract.</b> Two distinct lifecycle flags coexist on this row:
/// <list type="bullet">
///   <item><description>The inherited <see cref="AuditableEntity.IsActive"/> tracks
///     the "physical" soft-delete (audit trail eraser path — should remain rare).</description></item>
///   <item><description><see cref="RemovedAtUtc"/> is the "logical" attachment-link
///     soft-delete; the underlying <see cref="Document"/> row is NOT touched. The
///     citizen can remove an attachment from their application without erasing
///     the document from MinIO. The composite unique
///     (<see cref="ApplicationId"/>, <see cref="DocumentId"/>) WHERE
///     <c>RemovedAtUtc IS NULL</c> enforces "at most one active link per
///     (application, document)" while allowing the citizen to re-attach the same
///     document after removing it.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Virus-scan gate.</b> Born <see cref="AttachmentVirusScanStatus.Pending"/>.
/// The virus-scan worker flips the row to a terminal status; only
/// <see cref="AttachmentVirusScanStatus.Clean"/> /
/// <see cref="AttachmentVirusScanStatus.Skipped"/> rows participate in
/// downstream consumption (decision-pack rendering, citizen download).
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The <see cref="AuditableEntity.Id"/> primary key is
/// exposed on the DTO as a Sqid string per CLAUDE.md RULE 3 — hence the
/// <see cref="IExternalId"/> marker.
/// </para>
/// </remarks>
public sealed class ApplicationAttachment : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="ServiceApplication"/>.</summary>
    public long ApplicationId { get; set; }

    /// <summary>Navigation to the parent application row.</summary>
    public ServiceApplication? Application { get; set; }

    /// <summary>FK to the linked <see cref="Document"/> row (the binary lives in MinIO).</summary>
    public long DocumentId { get; set; }

    /// <summary>Navigation to the linked document.</summary>
    public Document? Document { get; set; }

    /// <summary>
    /// Semantic category — see <see cref="ApplicationAttachmentCategory"/>. Independent of
    /// MIME / extension; the same PDF can be either an
    /// <see cref="ApplicationAttachmentCategory.Income"/> proof or a
    /// <see cref="ApplicationAttachmentCategory.MedicalReport"/> report.
    /// </summary>
    public ApplicationAttachmentCategory Category { get; set; }

    /// <summary>
    /// Snapshot of "was this document type mandatory at attach time?" — captured
    /// from the service-passport's intake checklist at attach time, independent
    /// of any later passport republish. See remarks for the rationale.
    /// </summary>
    public bool IsMandatorySnapshot { get; set; }

    /// <summary>
    /// FK to the <see cref="UserProfile"/> primary id of the user who attached
    /// this document. For citizen-side attachments this is the Solicitant's user
    /// profile id; for examiner-side attachments it is the examiner's id.
    /// </summary>
    public long AttachedByUserId { get; set; }

    /// <summary>
    /// UTC instant the attachment link was created. Distinct from
    /// <see cref="AuditableEntity.CreatedAtUtc"/> so the business-event timestamp
    /// survives a row-level re-bake.
    /// </summary>
    public DateTime AttachedAtUtc { get; set; }

    /// <summary>Current virus-scan lifecycle state. Born <see cref="AttachmentVirusScanStatus.Pending"/>.</summary>
    public AttachmentVirusScanStatus VirusScanStatus { get; set; } = AttachmentVirusScanStatus.Pending;

    /// <summary>UTC instant the virus scan completed; <c>null</c> while <see cref="VirusScanStatus"/> is Pending.</summary>
    public DateTime? VirusScannedAtUtc { get; set; }

    /// <summary>Name / version tag of the scanner that produced the result (≤ 64 chars).</summary>
    public string? VirusScannerName { get; set; }

    /// <summary>Optional free-form annotation (≤ 500 chars). Do NOT log PII here.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// UTC instant the attachment link was logically removed; <c>null</c> while the
    /// link is active. Distinct from <see cref="AuditableEntity.IsActive"/> — see
    /// remarks for the dual-flag contract.
    /// </summary>
    public DateTime? RemovedAtUtc { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> of the operator that performed the removal; null while active.</summary>
    public long? RemovedByUserId { get; set; }

    /// <summary>Operator-supplied removal justification (3..500 chars when supplied).</summary>
    public string? RemovalReason { get; set; }
}
