using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0196 / TOR CF 23.02 — audit-category registry DTOs. All Id fields are
// Sqid-encoded per CLAUDE.md RULE 3. Contracts MUST NOT use <see cref="..."/>
// references into Cnas.Ps.Core — they live in a leaf assembly and only depend
// on themselves + Contracts.Security.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>R0196 / TOR CF 23.02 — outbound projection of an audit-category row.</summary>
/// <param name="Id">Sqid-encoded category id.</param>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE category code (e.g. AUTH, CRUD, DB_QUERY).</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity.</param>
/// <param name="IsActive">True when the category is selectable by operators / live audits.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AuditCategoryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DefaultSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive);

/// <summary>R0196 / TOR CF 23.02 — input envelope for creating an audit category.</summary>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE category code (≤ 64 chars; pattern <c>^[A-Z][A-Z0-9_.]{1,63}$</c>).</param>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 1000 chars).</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity (Information / Notice / Sensitive / Critical).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record AuditCategoryCreateInputDto(
    string Code,
    string DisplayName,
    string? Description,
    string DefaultSeverity);

/// <summary>R0196 / TOR CF 23.02 — input envelope for modifying an existing audit category.</summary>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="DefaultSeverity">Stable enum-name of the default severity.</param>
/// <param name="ChangeReason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record AuditCategoryModifyInputDto(
    string? DisplayName,
    string? Description,
    string? DefaultSeverity,
    string ChangeReason);

/// <summary>R0196 / TOR CF 23.02 — input envelope for reason-bearing transitions on the audit-category surface.</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AuditCategoryReasonInputDto(string Reason);

/// <summary>R0196 / TOR CF 23.02 — filter envelope for the audit-category list endpoint.</summary>
/// <param name="IsActive">Optional IsActive filter.</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record AuditCategoryFilterDto(
    bool? IsActive = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R0196 / TOR CF 23.02 — paged envelope returned by the audit-category list endpoint.</summary>
/// <param name="Items">Categories on the requested page.</param>
/// <param name="Total">Total matching categories across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AuditCategoryPageDto(
    IReadOnlyList<AuditCategoryDto> Items,
    int Total,
    int Skip,
    int Take);
