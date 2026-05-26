namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — persisted i18n key registry consulted by every
/// Blazor page, email template and admin notification banner. Each row is the canonical
/// home for a single translatable string identified by a stable kebab-case
/// <see cref="Code"/> (e.g. <c>pages.applications.list.title</c>); the per-language
/// text lives in <see cref="TranslationValue"/> rows linked via
/// <see cref="TranslationValue.TranslationKeyId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry, not resource files.</b> Resource files (.resx) bake the strings
/// into the WASM bundle at build time, which forces a full redeploy for every wording
/// fix. The translation tool requires that operators edit copy without touching code —
/// the resolver (<c>ITranslationResolver</c>) reads the in-memory snapshot rebuilt
/// from this table on a 60 s cadence, so wording changes apply within one refresh
/// tick of approval. The .resx files survive as the BACKUP fallback path the WASM
/// shell uses before the API responds (R0211 follow-up).
/// </para>
/// <para>
/// <b>Natural key.</b> <see cref="Code"/> is unique — operators address keys by their
/// stable kebab-case name from the admin UI, the resolver looks them up by code at
/// runtime, and the database UNIQUE index is the safety net against duplicate inserts.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the CRUD
/// service exposes keys over an admin REST surface; consumers reference rows by their
/// Sqid-encoded id when upserting per-language values, while the natural key
/// <see cref="Code"/> stays human-readable in the operator's URL bar.
/// </para>
/// </remarks>
public sealed class TranslationKey : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable kebab-case key (e.g. <c>pages.applications.list.title</c>,
    /// <c>emails.welcome.subject</c>). Must match the regex
    /// <c>^[a-z][a-z0-9.-]{1,127}$</c>; the validator and the EF column cap (128 chars)
    /// enforce. Code is the resolver's primary lookup handle so its shape is part of
    /// the public contract — renaming a code is a breaking change for every caller and
    /// every email-template variant that references it.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Optional free-form note giving translators context for the string ("appears on
    /// the dossier list above the table" / "use formal address — addressed to legal
    /// representatives"). Capped at 1024 characters at the EF mapping layer; nullable
    /// when no context is needed.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Coarse grouping label so the admin UI can filter by surface area (e.g.
    /// <c>"Public"</c>, <c>"Admin"</c>, <c>"Emails"</c>). Capped at 64 characters at
    /// the EF mapping layer; nullable for ungrouped keys (rare). The same vocabulary
    /// is used by <see cref="HelpTopic.Module"/> so the two tools share a single
    /// module filter in the operator console.
    /// </summary>
    public string? Module { get; set; }
}
