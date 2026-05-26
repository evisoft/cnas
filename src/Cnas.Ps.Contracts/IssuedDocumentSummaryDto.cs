using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0621 / TOR CF 13.02 — per-document summary row carried by
/// <see cref="ProfileOutput.IssuedDocuments"/>. Represents one document that
/// CNAS has issued (decision, certificate, extract, information leaflet) inside
/// a dossier owned by the calling user's Solicitant identity. The list is the
/// "issued docs" slice of the citizen profile aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issued vs. attached.</b> The aggregate includes only CNAS-issued
/// documents — citizen-supplied attachments (the <c>Attachment</c>
/// <c>DocumentKind</c> value) and internal notes (the <c>InternalNote</c>
/// value) are intentionally excluded. The kept kinds are Decision /
/// Certificate / Extract / Information. The crefs are plain strings
/// because Contracts may not reference Core (CLAUDE.md layer rule).
/// </para>
/// <para>
/// <b>Sqid invariant.</b> <see cref="Sqid"/> is the Sqid-encoded
/// <c>Document.Id</c> per CLAUDE.md RULE 3. The download URL is built off
/// the Sqid so a downstream consumer can deep-link without knowing the
/// internal long key.
/// </para>
/// <para>
/// <b>Newest-first ordering.</b> The producing service orders these by
/// <c>CreatedAtUtc DESC</c> and caps the list at 50 rows; older issuances
/// are intentionally not surfaced on the profile aggregate (consumers that
/// need full history use the dedicated documents registry endpoint).
/// </para>
/// </remarks>
/// <param name="Sqid">
/// Sqid-encoded id of the document — the opaque external handle per
/// CLAUDE.md RULE 3. Never <c>null</c>.
/// </param>
/// <param name="DocumentTypeCode">
/// Stable code describing the kind of document — values are the
/// <c>Cnas.Ps.Core.Domain.DocumentKind</c> enum names
/// (<c>Decision</c>, <c>Certificate</c>, <c>Extract</c>, <c>Information</c>).
/// </param>
/// <param name="Title">
/// Human-readable title of the document (the same string printed on the
/// dossier listing). Confidential — may carry the citizen's display name.
/// </param>
/// <param name="IssuedAtUtc">
/// UTC instant at which the document row was created. Sourced from
/// <c>AuditableEntity.CreatedAtUtc</c> (the canonical issuance timestamp).
/// </param>
/// <param name="Channel">
/// Issuance channel — <see cref="IssuedDocumentChannel.Electronic"/> when
/// the document carries an electronic signature
/// (<c>Document.IsSigned == true</c>), otherwise
/// <see cref="IssuedDocumentChannel.Paper"/>. Stable per row; never
/// recomputed after issuance.
/// </param>
/// <param name="Status">
/// Stable status string — <c>Active</c> for live issuances,
/// <c>Revoked</c> for soft-deleted rows. The aggregate surfaces only
/// active rows by default, but the field is kept on the wire so
/// downstream consumers always see an explicit value.
/// </param>
/// <param name="DownloadUrl">
/// Optional download URL the citizen can click to fetch the binary.
/// <c>null</c> when the document is bound to a non-public route or has
/// no canonical fetch endpoint; otherwise a relative path of the form
/// <c>/api/documents/{sqid}/download</c>.
/// </param>
public sealed record IssuedDocumentSummaryDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DocumentTypeCode,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime IssuedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IssuedDocumentChannel Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DownloadUrl);

/// <summary>
/// R0621 / TOR CF 13.02 — issuance channel for an
/// <see cref="IssuedDocumentSummaryDto"/>.
/// </summary>
/// <remarks>
/// Derived from <c>Document.IsSigned</c> on read — a signed document was
/// issued electronically, an unsigned one is treated as a paper issuance
/// (the document binary was produced by CNAS but the legally-binding copy
/// is the printed one delivered to the citizen). Stable across the
/// lifetime of the row.
/// </remarks>
public enum IssuedDocumentChannel
{
    /// <summary>Electronic — the document carries an MSign electronic signature.</summary>
    Electronic = 0,

    /// <summary>Paper — the document is the metadata stub for a printed copy.</summary>
    Paper = 1,
}
