using System.Collections.Generic;

namespace Cnas.Ps.Contracts;

/// <summary>Compact listing row for service passports (UC15).</summary>
/// <remarks>
/// R0142 / CF 15.04 — the list endpoint returns only the <c>IsCurrent=true</c> row for
/// each code, so <see cref="Version"/> identifies the catalogue revision exposed to
/// new submissions. Older versions remain queryable via the history endpoint.
/// </remarks>
public sealed record ServicePassportListItem(string Id, string Code, string NameRo, bool IsEnabled, int Version);

/// <summary>
/// Full passport details with form + workflow references.
/// <c>DecisionRulesJson</c> is the declarative rule-set consumed by the engine to
/// decide eligibility + amount; defaults to the empty object <c>"{}"</c>.
/// </summary>
/// <remarks>
/// R0142 / CF 15.04 — every passport revision is addressable by its Sqid id; the
/// <see cref="Version"/> column makes the revision identity explicit and the
/// <see cref="IsCurrent"/> flag tells consumers whether this is the active catalogue
/// row or a historical snapshot.
/// </remarks>
public sealed record ServicePassportDetailOutput(
    string Id,
    string Code,
    string NameRo,
    string? NameEn,
    string? NameRu,
    string DescriptionRo,
    string FormSchemaJson,
    string WorkflowCode,
    int MaxProcessingDays,
    bool IsEnabled,
    bool IsProactive,
    string DecisionRulesJson,
    int Version,
    bool IsCurrent);

/// <summary>
/// R0142 / CF 15.04 — history-list row for the GET <c>/api/service-passports/{sqid}/history</c>
/// endpoint. Carries the version chain metadata so the admin UI can render a timeline:
/// each row's Sqid id resolves to a full <see cref="ServicePassportDetailOutput"/> via
/// the standard detail endpoint.
/// </summary>
/// <param name="Id">Sqid id of THIS revision row (CLAUDE.md RULE 3).</param>
/// <param name="Code">Logical passport code (shared across all versions in the chain).</param>
/// <param name="Version">Monotonic version number — 1 on the original, N+1 on each republish.</param>
/// <param name="IsCurrent">True on exactly one row per code at any instant.</param>
/// <param name="CreatedAtUtc">When this revision was persisted.</param>
/// <param name="SupersededAtUtc">UTC instant the row stopped being current; null on the active row.</param>
public sealed record ServicePassportHistoryItem(
    string Id,
    string Code,
    int Version,
    bool IsCurrent,
    System.DateTime CreatedAtUtc,
    System.DateTime? SupersededAtUtc);

/// <summary>
/// Service-passport upsert input. <c>DecisionRulesJson</c> is the declarative rule-set
/// consumed by the engine to decide eligibility + amount; must not be null —
/// supply <c>"{}"</c> for passports without rules yet.
/// </summary>
public sealed record ServicePassportInput(
    string? Id,
    string Code,
    string NameRo,
    string? NameEn,
    string? NameRu,
    string DescriptionRo,
    string FormSchemaJson,
    string WorkflowCode,
    int MaxProcessingDays,
    bool IsEnabled,
    bool IsProactive,
    string DecisionRulesJson);

/// <summary>
/// R0143 / CF 17.19 — one mandatory-attachment row carried inside
/// <see cref="ServicePassportConfigMatrixDto.MandatoryAttachments"/>. Describes a
/// document-type code the applicant must supply together with the allowed
/// cardinality range.
/// </summary>
/// <param name="DocumentTypeCode">Stable classifier code (e.g. <c>ID_CARD</c>).</param>
/// <param name="CardinalityMin">Minimum number of attachments (typically 1).</param>
/// <param name="CardinalityMax">Maximum number of attachments (use <c>int.MaxValue</c> for "unbounded").</param>
public sealed record ServicePassportMandatoryAttachmentDto(
    string DocumentTypeCode,
    int CardinalityMin,
    int CardinalityMax);

/// <summary>
/// R0143 / CF 17.19 — one calc-formula row carried inside
/// <see cref="ServicePassportConfigMatrixDto.CalcFormulas"/>.
/// </summary>
/// <param name="Code">Named code referenced from the calculation pipeline (e.g. <c>monthlyBenefit</c>).</param>
/// <param name="Formula">Expression interpreted by <c>IExpressionEvaluator</c> (e.g. <c>base + bonus * 0.1</c>).</param>
public sealed record ServicePassportCalcFormulaDto(
    string Code,
    string Formula);

/// <summary>
/// R0143 / CF 17.19 — full configuration matrix for one service passport. Surfaces the
/// eight columns of the CF 17.19 contract:
/// Form + Validation + MandatoryAttachments + Receipt + DecisionTemplate +
/// FișaCalcul + CalcFormulas + ProcessingRules + PrintForm.
/// </summary>
/// <param name="Id">Sqid id of the addressed passport revision (CLAUDE.md RULE 3).</param>
/// <param name="Code">Stable passport code.</param>
/// <param name="Version">Revision number.</param>
/// <param name="FormSchemaJson">JSON-schema fragment describing the citizen-facing form.</param>
/// <param name="ValidationRulesJson">
/// Per-template metadata-driven validation rules JSON (sourced from the addressed
/// template via the decision/receipt template code). <see langword="null"/> when not yet
/// configured.
/// </param>
/// <param name="MandatoryAttachments">Mandatory-attachments matrix.</param>
/// <param name="ReceiptTemplateCode">Code of the receipt template (Recipisă).</param>
/// <param name="DecisionTemplateCode">Code of the decision template (Decizia).</param>
/// <param name="FisaCalculTemplateCode">Code of the Fișa de calcul template.</param>
/// <param name="CalcFormulas">Named calc-formula expressions.</param>
/// <param name="ProcessingRulesJson">Declarative processing-rules JSON (passport.DecisionRulesJson).</param>
/// <param name="PrintFormTemplateCode">Code of the print-form template (typically the Cerere).</param>
public sealed record ServicePassportConfigMatrixDto(
    string Id,
    string Code,
    int Version,
    string FormSchemaJson,
    string? ValidationRulesJson,
    IReadOnlyList<ServicePassportMandatoryAttachmentDto> MandatoryAttachments,
    string ReceiptTemplateCode,
    string DecisionTemplateCode,
    string FisaCalculTemplateCode,
    IReadOnlyList<ServicePassportCalcFormulaDto> CalcFormulas,
    string ProcessingRulesJson,
    string PrintFormTemplateCode);
