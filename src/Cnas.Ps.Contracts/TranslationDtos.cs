using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — admin-facing view of a single translation key.
/// All id fields are Sqid-encoded strings per CLAUDE.md RULE 3. The <c>Values</c>
/// collection carries every persisted per-language row so the operator UI can render
/// the full RO / EN / RU triplet in one round trip.
/// </summary>
/// <param name="Id">Sqid-encoded id of the translation key row.</param>
/// <param name="Code">Stable kebab-case key (e.g. <c>pages.applications.list.title</c>).</param>
/// <param name="Description">Optional translator-context note; nullable.</param>
/// <param name="Module">Coarse grouping label (e.g. <c>Public</c>, <c>Admin</c>); nullable.</param>
/// <param name="Values">All persisted per-language values for this key.</param>
public sealed record TranslationKeyDto(
    string Id,
    string Code,
    string? Description,
    string? Module,
    IReadOnlyList<TranslationValueDto> Values);

/// <summary>
/// R0210 — request payload for creating or updating a <see cref="TranslationKeyDto"/>.
/// Mass-assignment protection: contains only authoring fields, never ids or
/// approval flags.
/// </summary>
/// <param name="Code">Stable kebab-case key.</param>
/// <param name="Description">Optional translator-context note.</param>
/// <param name="Module">Optional grouping label.</param>
public sealed record TranslationKeyUpsertDto(
    string Code,
    string? Description,
    string? Module);

/// <summary>
/// R0210 — admin-facing view of a single localised translation value.
/// </summary>
/// <param name="Id">Sqid-encoded id of the translation value row.</param>
/// <param name="Language">ISO-639-1 code (<c>ro</c>/<c>en</c>/<c>ru</c>).</param>
/// <param name="Text">The localised text.</param>
/// <param name="IsApproved">Whether the value has been approved by a reviewer.</param>
/// <param name="TranslatorNote">Optional translator note for the reviewer.</param>
public sealed record TranslationValueDto(
    string Id,
    string Language,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Text,
    bool IsApproved,
    string? TranslatorNote);

/// <summary>
/// R0210 — request payload for upserting one (key, language) translation value. The
/// natural key (key Sqid + language) lives in the route; only the authoring fields
/// appear in the body.
/// </summary>
/// <param name="Text">The localised text (1..2000 chars).</param>
/// <param name="TranslatorNote">Optional translator note for the reviewer.</param>
public sealed record TranslationValueUpsertDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Text,
    string? TranslatorNote);
