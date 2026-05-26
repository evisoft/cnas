using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2003 — Template tri-lingual (RO/EN/RU) coverage DTOs (operational ops data)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2003 / R0133 — Filter envelope for the template language-coverage report
/// and the scheduled scan job. Operators may narrow the required-language set,
/// flip the approved-only filter off (treat unapproved variants as coverage),
/// include retired templates, and page the result.
/// </summary>
/// <param name="RequiredLanguages">
/// Lowercase ISO 639-1 codes that every template must have a (preferably
/// approved) variant for. When <c>null</c> or empty the service substitutes
/// the canonical <c>["ro", "en", "ru"]</c> set.
/// </param>
/// <param name="OnlyApproved">
/// When <c>true</c> (default) only approved variants count as "covered" — any
/// existing-but-unapproved row is reported as a gap. When <c>false</c> the
/// presence of a row (regardless of approval) is enough.
/// </param>
/// <param name="IncludeRetiredTemplates">
/// When <c>false</c> (default) the scan filters templates by
/// <c>IsActive=true</c>; when <c>true</c> retired templates are included so
/// operators can audit historical coverage too.
/// </param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..500.</param>
public sealed record TemplateLanguageCoverageFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string>? RequiredLanguages = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool OnlyApproved = true,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IncludeRetiredTemplates = false,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 100);

/// <summary>
/// R2003 / R0133 — Per-template gap row inside a
/// <see cref="TemplateLanguageCoverageReportDto"/>. Captures the languages
/// the template is missing approved variants for (when
/// <c>OnlyApproved=true</c>) or missing rows for entirely (when
/// <c>OnlyApproved=false</c>), plus the existing approved + unapproved sets
/// so the operator UI can render "what's already there" alongside "what's
/// missing".
/// </summary>
/// <param name="TemplateSqid">Sqid-encoded id of the parent template row (per CLAUDE.md RULE 3).</param>
/// <param name="TemplateCode">Stable kebab-case template code (not a Sqid — operator-facing identifier).</param>
/// <param name="TemplateNameDefault">Human-readable template name from <c>DocumentTemplate.Name</c>.</param>
/// <param name="DefaultLanguage">Stable lowercase ISO 639-1 default-language code from <c>DocumentTemplate.DefaultLanguage</c>.</param>
/// <param name="MissingLanguages">
/// Languages still missing from the template under the active filter. Sorted
/// alphabetically for deterministic ordering.
/// </param>
/// <param name="ExistingApprovedLanguages">Languages that already have an approved variant. Sorted.</param>
/// <param name="ExistingUnapprovedLanguages">
/// Languages where a variant row exists but has not yet been approved. Sorted.
/// Under <c>OnlyApproved=true</c> these also appear in
/// <see cref="MissingLanguages"/>; under <c>OnlyApproved=false</c> they count
/// as coverage.
/// </param>
public sealed record TemplateLanguageCoverageGapDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TemplateSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TemplateCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TemplateNameDefault,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DefaultLanguage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> MissingLanguages,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> ExistingApprovedLanguages,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> ExistingUnapprovedLanguages);

/// <summary>
/// R2003 / R0133 — Coverage report returned by the coverage service. Carries
/// the summary counters (scanned / fully-covered / with-gaps), the echoed
/// required-language set, the paged gap rows, and the canonical computation
/// timestamp.
/// </summary>
/// <param name="TotalTemplatesScanned">Total template rows the projection iterated.</param>
/// <param name="TotalTemplatesFullyCovered">Templates that have every required language covered under the active filter.</param>
/// <param name="TotalTemplatesWithGaps">Templates with at least one missing required language.</param>
/// <param name="RequiredLanguages">Echoed required-language set (canonicalised to lowercase).</param>
/// <param name="Gaps">Paged list of <see cref="TemplateLanguageCoverageGapDto"/> rows.</param>
/// <param name="Total">Total gap rows across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
/// <param name="ComputedAtUtc">UTC timestamp the projection was computed.</param>
public sealed record TemplateLanguageCoverageReportDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalTemplatesScanned,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalTemplatesFullyCovered,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int TotalTemplatesWithGaps,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> RequiredLanguages,
    IReadOnlyList<TemplateLanguageCoverageGapDto> Gaps,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime ComputedAtUtc);

/// <summary>
/// R2003 — One persisted coverage-finding row as it leaves the system.
/// Findings are de-duplicated per (TemplateId, MissingLanguage,
/// Acknowledged=false) so the open backlog has at most one row per
/// template-language gap.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the finding row.</param>
/// <param name="TemplateSqid">Sqid-encoded id of the parent template.</param>
/// <param name="TemplateCode">Stable kebab-case template code (operator-facing identifier).</param>
/// <param name="MissingLanguage">Lowercase ISO 639-1 code of the missing language.</param>
/// <param name="DetectedAt">UTC timestamp the gap was first detected.</param>
/// <param name="Acknowledged">Whether an operator has acknowledged the finding.</param>
/// <param name="AcknowledgedAt">UTC timestamp of the acknowledgement, when applicable.</param>
/// <param name="AcknowledgedByUserSqid">Sqid-encoded id of the acknowledging user, when applicable.</param>
/// <param name="AcknowledgementNote">Free-form acknowledgement note (3..1000 chars when set).</param>
public sealed record TemplateLanguageCoverageFindingDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TemplateSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TemplateCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string MissingLanguage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime DetectedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool Acknowledged,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? AcknowledgedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? AcknowledgedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? AcknowledgementNote);

/// <summary>
/// R2003 — Filter envelope for the open-findings list endpoint.
/// </summary>
/// <param name="Acknowledged">Optional acknowledgement-state filter — null matches both.</param>
/// <param name="MissingLanguage">Optional lowercase ISO 639-1 language filter — null matches any.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record TemplateLanguageCoverageFindingFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool? Acknowledged = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? MissingLanguage = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R2003 — Paged response envelope for the findings-list endpoint.
/// </summary>
/// <param name="Items">Findings page.</param>
/// <param name="Total">Total matching findings across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record TemplateLanguageCoverageFindingPageDto(
    IReadOnlyList<TemplateLanguageCoverageFindingDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R2003 — Acknowledgement payload for a coverage finding. The note is
/// expected to capture WHY the operator considers the gap handled (e.g.
/// "translation queued in batch X" / "template is RO-only by design").
/// </summary>
/// <param name="Note">Operator-supplied investigation note (3..1000 chars).</param>
public sealed record TemplateLanguageCoverageAcknowledgeInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Note);
