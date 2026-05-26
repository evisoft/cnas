using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0227 / TOR UI 014 — request body for <c>POST /api/attachments</c>. Carries the
/// caller-supplied bytes (base64-encoded), declared filename, owner reference, semantic
/// category, sensitivity label, and optional description. The uploader identity is
/// resolved server-side from the authenticated principal — the input DTO deliberately
/// omits the uploader field to prevent mass-assignment forgery (CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="OwnerEntityType">
/// Stable CLR-type name of the owning entity. Must be one of the values in the frozen
/// allow-list constants on
/// <c>Cnas.Ps.Application.Attachments.AttachmentOwnerTypes</c>.
/// </param>
/// <param name="OwnerSqid">
/// Sqid-encoded primary key of the owning entity. Decoded server-side; an unparseable
/// value yields <c>INVALID_SQID</c> (HTTP 400).
/// </param>
/// <param name="ContentBase64">
/// Base64-encoded file bytes. Validator enforces non-empty, base64 well-formedness, and
/// decoded byte length ≤ <c>AttachmentOptions.MaxBytes</c>. Required.
/// </param>
/// <param name="DeclaredFileName">
/// Original filename as the user supplied it. 1..255 chars, must contain an extension,
/// no path separators. The service sanitises this further before persisting.
/// </param>
/// <param name="Category">
/// Stable enum name (<c>Identity</c>, <c>Income</c>, <c>Medical</c>, <c>LegalDocument</c>,
/// <c>Photo</c>, <c>Other</c>). Validator rejects unknown values.
/// </param>
/// <param name="SensitivityLabel">
/// Stable enum name (<c>Public</c>, <c>Internal</c>, <c>Confidential</c>,
/// <c>Restricted</c>). Validator rejects unknown values. Default is
/// <c>Confidential</c> when null/empty.
/// </param>
/// <param name="Description">Optional free-form annotation (≤ 500 chars).</param>
public sealed record AttachmentUploadDto(
    string OwnerEntityType,
    string OwnerSqid,
    string ContentBase64,
    string DeclaredFileName,
    string Category,
    string? SensitivityLabel,
    string? Description);

/// <summary>
/// R0227 / TOR UI 014 — output projection of an <c>AttachmentRecord</c> row. The
/// <c>Bytes</c> payload is OMITTED from this DTO — call the download endpoint to fetch
/// the bytes. All ids are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity classification.</b> The class is annotated
/// <see cref="Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential"/> because a
/// typical attachment row carries citizen-facing metadata (filename, description) that
/// names the resource the citizen uploaded. The most-sensitive per-property labels apply
/// on top — see the individual property attributes.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded id of the attachment row.</param>
/// <param name="OwnerEntityType">Stable CLR-type name of the owning entity.</param>
/// <param name="OwnerSqid">Sqid-encoded primary key of the owning entity.</param>
/// <param name="FileName">Sanitised filename (lowercased / slugified, extension preserved).</param>
/// <param name="ContentType">Detected MIME type from the magic-byte sniff.</param>
/// <param name="SizeBytes">Persisted blob size in bytes.</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 of the bytes (integrity anchor).</param>
/// <param name="Category">Stable category-enum name (<c>Identity</c>, <c>Income</c>, ...).</param>
/// <param name="SensitivityLabel">
/// Stable sensitivity-label enum name (<c>Public</c>, <c>Internal</c>, <c>Confidential</c>,
/// <c>Restricted</c>). Drives downstream visual badge rendering.
/// </param>
/// <param name="Description">Optional uploader-supplied annotation.</param>
/// <param name="UploadedByUserSqid">Sqid-encoded uploader id.</param>
/// <param name="UploadedUtc">UTC upload timestamp.</param>
/// <param name="IsArchived">Soft-archive flag.</param>
[SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential)]
public sealed record AttachmentRecordDto(
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    string OwnerEntityType,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Public)]
    string OwnerSqid,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential)]
    string FileName,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    string ContentType,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    long SizeBytes,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    string Sha256Hex,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    string Category,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    string SensitivityLabel,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential)]
    string? Description,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Public)]
    string UploadedByUserSqid,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    DateTime UploadedUtc,
    [property: SensitivityClassification(Cnas.Ps.Contracts.Security.SensitivityLabel.Internal)]
    bool IsArchived);

/// <summary>
/// R0227 / TOR UI 014 — payload returned by the download endpoint. Carries the raw
/// bytes, the detected content type for the response <c>Content-Type</c> header, and
/// the sanitised filename for the <c>Content-Disposition</c> header. Never persisted —
/// constructed in-memory immediately before the API hands it to <c>FileContentResult</c>.
/// </summary>
/// <param name="Bytes">Raw file bytes streamed back to the caller.</param>
/// <param name="ContentType">Detected MIME type stored on the row.</param>
/// <param name="FileName">Sanitised filename for <c>Content-Disposition</c>.</param>
public sealed record AttachmentDownloadDto(
    byte[] Bytes,
    string ContentType,
    string FileName);
