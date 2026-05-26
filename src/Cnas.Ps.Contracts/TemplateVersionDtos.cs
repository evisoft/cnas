namespace Cnas.Ps.Contracts;

/// <summary>
/// R0132 / CF 17.18 — paged listing of historical versions for a single
/// <see cref="DocumentTemplateDto"/> code. Returned by
/// <c>GET /api/admin/templates/{code}/versions</c>.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b> Template catalog metadata is administrator-only.
/// </remarks>
/// <param name="Code">Stable kebab-case template code shared by every row in the page.</param>
/// <param name="Items">The page of versions, ordered Version DESC.</param>
/// <param name="TotalCount">Total number of historical versions for the code.</param>
public sealed record TemplateVersionPageDto(
    string Code,
    System.Collections.Generic.IReadOnlyList<DocumentTemplateDto> Items,
    long TotalCount);

/// <summary>
/// R0132 / CF 17.18 — one row in the version-listing page. Mirrors
/// <c>Cnas.Ps.Core.Domain.DocumentTemplate</c> at the boundary with Sqid-encoded id.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b>
/// </remarks>
/// <param name="Id">Sqid-encoded surrogate id of the version row.</param>
/// <param name="Code">Stable kebab-case template code.</param>
/// <param name="Name">Human-readable template name.</param>
/// <param name="Description">Free-text description; null when omitted.</param>
/// <param name="Version">Monotonic version number.</param>
/// <param name="IsCurrent">True for exactly one row per code.</param>
/// <param name="ContentSha256">SHA-256 hex digest of the stored binary at upload time.</param>
/// <param name="ContentLength">Size of the stored binary in bytes.</param>
/// <param name="CreatedAtUtc">UTC instant the row was inserted.</param>
public sealed record DocumentTemplateDto(
    string Id,
    string Code,
    string Name,
    string? Description,
    int Version,
    bool IsCurrent,
    string ContentSha256,
    long ContentLength,
    System.DateTime CreatedAtUtc);

/// <summary>
/// R0132 / CF 17.18 — structured diff between two versions of the same template code.
/// Returned by <c>GET /api/admin/templates/versions/{baseline}/diff/{current}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity: Internal.</b>
/// </para>
/// <para>
/// The diff compares the metadata + content hash of each version; bodies are NOT
/// inlined (a template can be tens of megabytes). Each <see cref="TemplateVersionDiffEntryDto"/>
/// reports a field path that has changed between the two versions, along with the
/// before / after string values rendered for display.
/// </para>
/// </remarks>
/// <param name="BaselineVersion">Monotonic version number of the baseline (earlier).</param>
/// <param name="CurrentVersion">Monotonic version number of the current (later) reference.</param>
/// <param name="Code">Stable template code shared by both versions.</param>
/// <param name="Entries">Field-level diff entries; empty when the two versions are byte-equivalent.</param>
public sealed record TemplateVersionDiffDto(
    int BaselineVersion,
    int CurrentVersion,
    string Code,
    System.Collections.Generic.IReadOnlyList<TemplateVersionDiffEntryDto> Entries);

/// <summary>
/// R0132 / CF 17.18 — one field-level diff entry between two versions.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b>
/// </remarks>
/// <param name="FieldPath">
/// Dotted field path being compared (e.g. <c>"Name"</c>, <c>"ContentSha256"</c>,
/// <c>"ContentLength"</c>, <c>"Description"</c>). Stable across versions.
/// </param>
/// <param name="ChangeKind">
/// One of <c>"Added"</c> (baseline null, current non-null), <c>"Removed"</c> (baseline
/// non-null, current null), <c>"Modified"</c> (both non-null but different).
/// </param>
/// <param name="BaselineValue">Stringified value on the baseline version; null when missing.</param>
/// <param name="CurrentValue">Stringified value on the current version; null when missing.</param>
public sealed record TemplateVersionDiffEntryDto(
    string FieldPath,
    string ChangeKind,
    string? BaselineValue,
    string? CurrentValue);

/// <summary>
/// R0132 / CF 17.18 — input DTO carrying the reason for a template rollback. The service
/// requires the caller to justify rolling back a template to a prior version; the reason
/// is persisted on the audit row.
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b>
/// </remarks>
/// <param name="Reason">Free-text justification (3..500 chars).</param>
public sealed record TemplateRollbackInputDto(
    string Reason);
