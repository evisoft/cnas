namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0225 / TOR UI 015 — per-page (or per-section) help topic registry consulted by
/// the contextual-help widget on every Blazor page. Each row identifies a topic by
/// a stable kebab-case <see cref="Code"/> (e.g.
/// <c>pages.applications.new.applicant-section</c>); the per-language title + body
/// live in <see cref="HelpTopicTranslation"/> rows linked via
/// <see cref="HelpTopicTranslation.HelpTopicId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry, not inline copy.</b> Inline help baked into Blazor markup
/// requires a redeploy for every editorial fix. The registry lets operators tune
/// the wording from the admin UI; the resolver (<c>IHelpResolver</c>) reads the
/// in-memory snapshot rebuilt from this table on a 60 s cadence so edits apply
/// within one refresh tick of approval.
/// </para>
/// <para>
/// <b>Natural key.</b> <see cref="Code"/> is unique — operators address topics by
/// their stable kebab-case name and the database UNIQUE index is the safety net.
/// </para>
/// <para>
/// <b>Anchor binding.</b> <see cref="AnchorSelector"/> is the CSS selector the UI
/// binds the help-tooltip to (e.g. <c>#applicant-section</c>); when null the topic
/// surfaces only via the page-level help widget and is not auto-attached to a
/// specific DOM element.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the
/// CRUD service exposes topics over an admin REST surface; consumers reference rows
/// by their Sqid-encoded id when upserting per-language translations.
/// </para>
/// </remarks>
public sealed class HelpTopic : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable kebab-case topic code (e.g.
    /// <c>pages.applications.new.applicant-section</c>). Must match the regex
    /// <c>^[a-z][a-z0-9.-]{1,127}$</c>; the validator and the EF column cap (128 chars)
    /// enforce. Same shape as <see cref="TranslationKey.Code"/> so the two surfaces
    /// share a single naming convention.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Coarse grouping label (e.g. <c>"Public"</c>, <c>"Admin"</c>) shared with
    /// <see cref="TranslationKey.Module"/>. Capped at 64 characters at the EF
    /// mapping layer. Required for help topics because the admin UI groups by
    /// module to keep the registry navigable.
    /// </summary>
    public required string Module { get; set; }

    /// <summary>
    /// Optional CSS selector that the UI binds the contextual-help tooltip to
    /// (e.g. <c>#applicant-section</c>). When non-null the help widget renders an
    /// inline anchor next to the matched element; when null the topic surfaces only
    /// via the page-level help index. Capped at 256 characters at the EF mapping
    /// layer.
    /// </summary>
    public string? AnchorSelector { get; set; }
}
