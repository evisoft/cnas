namespace Cnas.Ps.Core.Domain;

/// <summary>
/// UC17 phase 2A — Operator-uploaded DOCX template persisted to PostgreSQL + MinIO. Each
/// row is one immutable revision of the binary for a given <see cref="Code"/>. The
/// repository is append-only: a new upload for an existing code inserts a new row with
/// <see cref="Version"/> = previous + 1 and flips <see cref="IsCurrent"/> on the previous
/// current row to <c>false</c>. Historical revisions remain queryable for audit and
/// rollback. Mirrors the versioning shape of <see cref="WorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistent vs DI-baked templates.</b> Phase 1 (batch #94) shipped the read-only
/// catalog over the 35 DI-baked <c>IDocxTemplate</c> singletons. Phase 2A introduces a
/// parallel persistence path so operators can upload templates without re-deploying. The
/// admin surface exposes both as <c>Cnas.Ps.Contracts.TemplateCatalogEntry</c>
/// rows differentiated by their <c>Source</c> field; when a <see cref="Code"/> collides
/// between the two registries the persistent row wins (operator-override semantics) —
/// see the service-layer documentation for the rationale.
/// </para>
/// <para>
/// <b>Identifier convention.</b> The <see cref="Code"/> is a stable kebab-case string
/// (e.g. <c>decizia-pensie</c>) — NOT a Sqid. The same RULE 3 exception that applies
/// to <see cref="WorkflowDefinition.Code"/> and the DI-baked
/// <c>Cnas.Ps.Contracts.TemplateCatalogEntry.Code</c> applies here for the same
/// reason: the code is the externally-known identifier, not a sequential surrogate key.
/// </para>
/// <para>
/// <b>Storage decoupling.</b> The binary lives in MinIO under
/// <see cref="StorageObjectKey"/>; the row carries only metadata + the SHA-256 digest.
/// On download the service fetches the blob, verifies the SHA-256, and streams it to
/// the caller. The <see cref="ContentLength"/> field is materialised at upload time so
/// the catalog list endpoint can show file sizes without round-tripping to MinIO.
/// </para>
/// <para>
/// <b>Rendering deferred to phase 2B.</b> Persisted rows carry the metadata the renderer
/// pipeline will need (code, version, current flag, blob coordinates) but the
/// generation service does not yet dispatch to them. Phase 2B will unify the dispatch
/// so uploaded templates render through the same code path as DI-baked ones.
/// </para>
/// </remarks>
public sealed class DocumentTemplate : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable kebab-case template identifier (e.g. <c>decizia-pensie</c>). Unique across
    /// all currently-active rows when paired with <see cref="Version"/>; at most one row
    /// per code carries <see cref="IsCurrent"/> = <c>true</c>. Canonicalised to lower-case
    /// at write time so lookups are case-insensitive.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Human-readable display name shown in the admin UI's template-picker drop-down and
    /// in the catalog listing. Free-text (single line) — distinct from <see cref="Code"/>
    /// which is the machine-readable identifier.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Free-text purpose / usage note rendered alongside the template in the catalog. May
    /// be <see langword="null"/> when the operator omits the description on upload.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Monotonically increasing version per <see cref="Code"/>. The first upload for a
    /// code inserts <c>1</c>; each subsequent upload inserts the previous maximum + 1.
    /// Mirrors the <see cref="WorkflowDefinition"/> versioning model.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// True for exactly one row per <see cref="Code"/> at any instant — the "current"
    /// version served by <c>DownloadAsync</c> and surfaced through the catalog. Older
    /// revisions remain in the table with <see cref="IsCurrent"/> = <c>false</c> for
    /// audit and rollback. Distinct from <see cref="AuditableEntity.IsActive"/> — a row
    /// may be both <see cref="AuditableEntity.IsActive"/> = <c>true</c> (not soft-deleted) and
    /// <see cref="IsCurrent"/> = <c>false</c> (superseded).
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// MinIO object key (path within the configured bucket) where the binary lives. The
    /// service composes this as <c>templates/{code}/v{version}/{code}.docx</c> so the
    /// blob layout is human-navigable when inspected directly through the MinIO console.
    /// </summary>
    public required string StorageObjectKey { get; set; }

    /// <summary>
    /// MIME type of the uploaded blob — always
    /// <c>application/vnd.openxmlformats-officedocument.wordprocessingml.document</c>
    /// in phase 2A. Retained as a column so a future relaxation of the upload validator
    /// (e.g. accepting <c>.docm</c> macros for an opt-in template class) does not
    /// require a migration.
    /// </summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// Size of the uploaded blob in bytes. Capped at the service-layer
    /// <c>MaxTemplateSize</c> constant; rows whose size exceeds the cap cannot be
    /// produced through the normal upload path (the validator rejects them with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.FileTooLarge"/>).
    /// </summary>
    public long ContentLength { get; set; }

    /// <summary>
    /// SHA-256 hex digest (lower-case, 64 chars) of the uploaded blob — computed by the
    /// service as it streams the upload into MinIO. Used to verify integrity on
    /// download: if the bytes read back from MinIO do not hash to this value the row
    /// is considered tampered and the download fails.
    /// </summary>
    public required string ContentSha256 { get; set; }

    /// <summary>
    /// R0133 / CF 17.16 — Stable ISO-639-1 lower-case fallback language for this template.
    /// Defaults to <c>"ro"</c> (the original authoring locale of the 35 baked-in
    /// templates). When a render request targets a locale whose <see cref="TemplateVariant"/>
    /// either does not exist or is not approved, the renderer falls back to the variant
    /// whose <see cref="TemplateVariant.Language"/> matches this column. Always populated;
    /// the migration back-fills <c>"ro"</c> for every pre-existing row.
    /// </summary>
    public string DefaultLanguage { get; set; } = "ro";

    /// <summary>
    /// R0131 / CF 17.15 — JSON array of metadata-driven validation rules applied to the
    /// form values supplied at render time. Optional and nullable: legacy rows behave
    /// unchanged (no validation rules ⇒ <see langword="null"/> ⇒ every render passes the
    /// validation gate). When populated, the shape is a JSON array of
    /// <c>{ "fieldName": "...", "ruleKind": "Required|MaxLength|MinLength|Regex|Range|Custom", "argument": "..." }</c>
    /// rows interpreted by
    /// <c>Cnas.Ps.Application.Templates.ITemplateValidationService</c>. See the
    /// <see cref="Cnas.Ps.Core.ValueObjects.TemplateValidationRule"/> value object for the
    /// per-rule contract.
    /// </summary>
    public string? ValidationRulesJson { get; set; }
}
