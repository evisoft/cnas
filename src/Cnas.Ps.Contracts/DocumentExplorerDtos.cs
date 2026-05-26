using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0671 continuation — request body for <c>POST /api/documents/search</c>. Mirrors the
/// canonical search-envelope shape used by <see cref="AuditLogSearchInput"/> (R0193) and
/// the rest of the QBE-driven explorer endpoints: optional QBE filter, optional UTC
/// date range, paging window. Validation is delegated to
/// <c>DocumentsListInputValidator</c>; QBE envelope validation rides through the
/// converter as documented on <see cref="QbeFilterDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paging cap.</b> The service clamps <see cref="Take"/> to a hard ceiling of 200
/// (matches every other QBE explorer endpoint). The validator rejects wire values above
/// the cap so the UI sees the rejection before the request reaches the service layer.
/// </para>
/// <para>
/// <b>QBE envelope.</b> Reuses <see cref="QbeFilterDto"/> against the
/// <c>Document</c> registry; queryable field set is published by
/// <c>IQbeRegistrySchemaProvider</c> at startup.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> The envelope carries no raw database identifiers — every
/// outbound row in <see cref="DocumentsListPageDto"/> exposes Sqid-encoded ids per
/// CLAUDE.md RULE 3.
/// </para>
/// </remarks>
/// <param name="Filter">Optional QBE envelope; null treated as "no QBE filter".</param>
/// <param name="FromUtc">
/// Inclusive lower bound on the document's <c>CreatedAtUtc</c>. When both
/// <see cref="FromUtc"/> and <see cref="ToUtc"/> are supplied the validator
/// enforces <see cref="FromUtc"/> ≤ <see cref="ToUtc"/>.
/// </param>
/// <param name="ToUtc">
/// Exclusive upper bound on the document's <c>CreatedAtUtc</c>.
/// </param>
/// <param name="Skip">Zero-based row offset; validator rejects negatives.</param>
/// <param name="Take">Maximum rows to return; server cap = 200.</param>
public sealed record DocumentsListInput(
    QbeFilterDto? Filter = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R0671 continuation — paged response envelope for <c>POST /api/documents/search</c>.
/// Mirrors <see cref="AuditLogPageDto"/> shape: row list + total count. The total comes
/// from the budget verdict's <c>EstimatedRowCount</c> so a second COUNT round-trip is
/// avoided.
/// </summary>
/// <remarks>
/// Type-level sensitivity floor is <see cref="SensitivityLabel.Internal"/> — document
/// metadata is not freely shareable across organisations even though individual file
/// names are stamped <see cref="SensitivityLabel.Confidential"/> per R0228.
/// </remarks>
/// <param name="Items">Materialised rows for the requested page.</param>
/// <param name="TotalCount">Total rows matching the filter (server-evaluated).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DocumentsListPageDto(
    IReadOnlyList<DocumentListItemDto> Items,
    int TotalCount);

/// <summary>
/// R0671 continuation — single-row projection for the document-registry list. All
/// identifiers are Sqid-encoded per CLAUDE.md RULE 3; the document's display title
/// (mapped from the source row's <c>Title</c>) carries
/// <see cref="SensitivityLabel.Confidential"/> because the citizen-supplied original
/// filename is treated as PII-bearing (a beneficiary surname commonly appears in scan
/// filenames).
/// </summary>
/// <param name="Id">Sqid-encoded document primary key.</param>
/// <param name="OwnerEntityType">
/// Stable string describing the polymorphic owner. Currently always <c>"Dossier"</c>
/// when the document is dossier-attached and <see langword="null"/> for unattached
/// templates (matches the source row's <c>DossierId</c> nullability). The shape is
/// forward-compatible with the R0227 attachment-record model.
/// </param>
/// <param name="OwnerEntitySqid">
/// Sqid-encoded owner identifier — i.e. the Sqid-encoded dossier id when the document
/// is dossier-attached, otherwise <see langword="null"/>.
/// </param>
/// <param name="DocumentKind">
/// Stable enum-name string for the document's <c>Kind</c> (e.g. <c>"Attachment"</c>,
/// <c>"Decision"</c>, <c>"Certificate"</c>).
/// </param>
/// <param name="FileName">
/// Display name surfaced to the operator. Mapped from the document's <c>Title</c>;
/// marked Confidential because the citizen-supplied original filename frequently
/// embeds PII (beneficiary surname, IDNP, etc.).
/// </param>
/// <param name="MimeType">Validated MIME type (SEC 010 magic-byte sniff).</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="CreatedAtUtc">UTC timestamp the row was created.</param>
/// <param name="IssuedByUserSqid">
/// Sqid-encoded user id of the operator / system identifier that issued the
/// document, mapped from <c>AuditableEntity.CreatedBy</c>. <see langword="null"/>
/// when the source row carries no <c>CreatedBy</c>.
/// </param>
public sealed record DocumentListItemDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? OwnerEntityType,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? OwnerEntitySqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DocumentKind,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FileName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string MimeType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long SizeBytes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? IssuedByUserSqid);
