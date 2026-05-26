using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0225 / TOR UI 015 — admin-facing view of a single contextual-help topic. All id
/// fields are Sqid-encoded strings per CLAUDE.md RULE 3. The
/// <see cref="Translations"/> collection carries every persisted per-language row so
/// the operator UI and the runtime help widget can both consume one envelope.
/// </summary>
/// <param name="Id">Sqid-encoded id of the help topic row.</param>
/// <param name="Code">Stable kebab-case code (e.g. <c>pages.applications.new.applicant-section</c>).</param>
/// <param name="Module">Coarse grouping label (e.g. <c>Public</c>, <c>Admin</c>).</param>
/// <param name="AnchorSelector">Optional CSS selector the UI binds the help tooltip to; nullable.</param>
/// <param name="IsActive">Soft-delete flag; <c>true</c> for live rows.</param>
/// <param name="Translations">All persisted per-language translations for this topic.</param>
public sealed record HelpTopicDto(
    string Id,
    string Code,
    string Module,
    string? AnchorSelector,
    bool IsActive,
    IReadOnlyList<HelpTopicTranslationDto> Translations);

/// <summary>
/// R0225 — request payload for creating or updating a <see cref="HelpTopicDto"/>.
/// Mass-assignment protection: contains only authoring fields, never ids or
/// audit metadata.
/// </summary>
/// <param name="Code">Stable kebab-case topic code.</param>
/// <param name="Module">Coarse grouping label.</param>
/// <param name="AnchorSelector">Optional CSS selector for tooltip binding.</param>
public sealed record HelpTopicUpsertDto(
    string Code,
    string Module,
    string? AnchorSelector);

/// <summary>
/// R0225 — admin-facing view of a single localised help translation.
/// </summary>
/// <param name="Id">Sqid-encoded id of the translation row.</param>
/// <param name="Language">ISO-639-1 code (<c>ro</c>/<c>en</c>/<c>ru</c>).</param>
/// <param name="Title">Tooltip / dialog title.</param>
/// <param name="BodyMarkdown">Markdown body (max 20_000 chars).</param>
/// <param name="IsApproved">Whether the translation has been approved by a reviewer.</param>
/// <param name="TranslatorNote">Optional translator note; nullable.</param>
public sealed record HelpTopicTranslationDto(
    string Id,
    string Language,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BodyMarkdown,
    bool IsApproved,
    string? TranslatorNote);

/// <summary>
/// R0225 — request payload for upserting one (topic, language) translation. The
/// natural key (topic Sqid + language) lives in the route; only the authoring fields
/// appear in the body.
/// </summary>
/// <param name="Title">Tooltip / dialog title (1..200 chars).</param>
/// <param name="BodyMarkdown">Markdown body (1..20_000 chars).</param>
/// <param name="TranslatorNote">Optional translator note.</param>
public sealed record HelpTopicTranslationUpsertDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BodyMarkdown,
    string? TranslatorNote);
