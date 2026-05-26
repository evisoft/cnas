using System.Collections.Generic;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0183 / SEC 043 — admin-configurable per-entity policy that controls which property
/// changes on a mutating save are worth emitting an audit row for, and which property
/// values must be redacted out of the resulting before/after diff payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this entity exists.</b> Before R0183 every mutating save on an auditable
/// entity produced an audit row regardless of whether any business-meaningful field
/// actually changed (a no-op resave from a hot-path refresh, a hash-recompute, a
/// timestamp-only update). SEC 043 says: log only meaningful diffs, and capture
/// exactly what changed. R0183 introduces a per-entity policy table that the service
/// layer consults before calling <c>IAuditDiffWriter.WriteIfDiffAsync</c> — if a
/// policy exists and <see cref="RequireAnyChange"/> is true, the writer emits a row
/// ONLY when at least one tracked field actually differs between the before snapshot
/// and the after snapshot.
/// </para>
/// <para>
/// <b>Sibling to <see cref="AuditPolicy"/>.</b> The R0182 <see cref="AuditPolicy"/>
/// table tunes severity / suppression / extra-redact-keys on the audit pipeline's
/// hot path AFTER an event is enqueued. <see cref="AuditFieldPolicy"/> operates one
/// layer earlier: it decides whether the call site should enqueue at all and, when
/// it does, attaches a structured before/after diff. Both tables can co-exist on the
/// same event — the field policy decides "do we write?", the audit policy decides
/// "what severity / where does the row go?".
/// </para>
/// <para>
/// <b>EntityType natural key.</b> The CLR short name (e.g. <c>Solicitant</c>,
/// <c>ServiceApplication</c>, <c>UserProfile</c>) — never the assembly-qualified
/// name. Operators key policies by this string in runbooks. The unique index on
/// <see cref="EntityType"/> guarantees one policy per CLR type. The validator
/// enforces a tight ASCII shape (<c>^[A-Z][A-Za-z0-9]{2,63}$</c>) so an admin cannot
/// accidentally configure <c>"solicitant"</c> (lowercase) — that would silently
/// never match because the diff writer keys off the runtime type's <c>Name</c>.
/// </para>
/// <para>
/// <b>TrackedFields vs SuppressedFields semantics.</b> A property listed in
/// <see cref="TrackedFields"/> participates in change detection — its before/after
/// values are compared, and a difference triggers a diff entry. A property listed
/// in <see cref="SuppressedFields"/> never appears in the diff payload — its value
/// is redacted to the literal string <c>"[redacted]"</c>. Overlap is permitted and
/// meaningful: a field listed in BOTH lists is tracked for trigger purposes (its
/// change still emits an audit row) but its actual before/after values are redacted
/// in the persisted diff. This is the right shape for things like <c>NationalId</c>
/// — operators want to know that the IDNP changed, but the actual digits must
/// never reach the audit table.
/// </para>
/// <para>
/// <b>RequireAnyChange safeguard.</b> When <c>true</c>, the writer skips the audit
/// row entirely if no tracked field differs between the before and after snapshots.
/// When <c>false</c>, every mutating save emits a row regardless of diff — useful
/// for entities where the act of touching is itself the audit-worthy event (audit
/// log itself, etc.). The validator additionally requires at least one
/// <see cref="TrackedFields"/> entry when <see cref="RequireAnyChange"/> is true —
/// the combination "require any change but never look at any field" would silently
/// suppress every row.
/// </para>
/// <para>
/// <b>Soft delete = disabled.</b> Inherits the standard <see cref="AuditableEntity.IsActive"/>
/// flag. Operators disable a policy by setting that flag (or <see cref="IsEnabled"/>)
/// to false; the resolver skips rows where either is false. The CRUD service's
/// <c>DisableAsync</c> flips both flags and emits a Critical mutation audit row.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the CRUD
/// service exposes policies over an admin REST surface and consumers reference rows
/// by their Sqid-encoded id — see <c>IAuditFieldPolicyService</c> /
/// <c>AuditFieldPoliciesController</c>. The natural <see cref="EntityType"/> is the
/// stable cross-environment handle used in admin URL paths; the Sqid is the
/// wire-level identifier for mutating operations (Update / Disable).
/// </para>
/// </remarks>
public sealed class AuditFieldPolicy : AuditableEntity, IExternalId
{
    /// <summary>
    /// CLR short name of the entity this policy governs (e.g. <c>Solicitant</c>,
    /// <c>ServiceApplication</c>, <c>UserProfile</c>). The diff writer keys off the
    /// runtime type's <c>Type.Name</c>, so casing matters — the
    /// validator regex (<c>^[A-Z][A-Za-z0-9]{2,63}$</c>) enforces PascalCase to
    /// catch typos at write time. Capped at 64 characters at the EF mapping layer.
    /// Unique index — one row per CLR type.
    /// </summary>
    public required string EntityType { get; set; }

    /// <summary>
    /// Property names whose value changes between the before and after snapshots
    /// trigger an audit row (when <see cref="RequireAnyChange"/> is true) and
    /// appear as entries in the persisted diff payload. Examples:
    /// <c>["DisplayName","Email","PhoneE164"]</c>. Empty list is allowed only when
    /// <see cref="RequireAnyChange"/> is false (the validator enforces).
    /// </summary>
    /// <remarks>
    /// Property names are exact CLR member names — the diff computer reflects against
    /// the supplied entity type, so a name typo means "field is silently never
    /// tracked". Operators should mirror the entity's declared property names
    /// verbatim.
    /// </remarks>
    public List<string> TrackedFields { get; set; } = new();

    /// <summary>
    /// Property names whose values must NEVER appear in the persisted diff payload —
    /// when a property listed here changes (and is also tracked), the diff entry
    /// records the change but the value is replaced with the literal
    /// <c>"[redacted]"</c>. Examples: <c>["NationalId","NationalIdHash","LocalPasswordHash"]</c>.
    /// </summary>
    /// <remarks>
    /// Overlap with <see cref="TrackedFields"/> is meaningful: a property in BOTH
    /// lists triggers a diff row on change but the value is redacted in the payload.
    /// A property in <see cref="SuppressedFields"/> ONLY (not in
    /// <see cref="TrackedFields"/>) is never compared and never emitted — equivalent
    /// to not listing it at all from the writer's perspective.
    /// </remarks>
    public List<string> SuppressedFields { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, the writer emits an audit row ONLY when at least one tracked
    /// field actually differs between the before and after snapshots. When
    /// <c>false</c>, every mutating save emits a row (the diff payload is still
    /// computed and attached). Defaults to <c>true</c> — the SEC 043 default.
    /// </summary>
    public bool RequireAnyChange { get; set; } = true;

    /// <summary>
    /// Severity stamped on the audit row when the diff writer emits it. The
    /// R0182 <see cref="AuditPolicy"/> resolver may further override this on the
    /// hot path, but this is the default the writer attaches.
    /// </summary>
    public AuditSeverity Severity { get; set; } = AuditSeverity.Notice;

    /// <summary>
    /// Enable flag distinct from <see cref="AuditableEntity.IsActive"/>. Both are
    /// AND-ed at resolve time so operators can soft-disable a policy without
    /// soft-deleting the row, leaving CRUD history intact for the audit explorer
    /// (R0193).
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Free-form admin-facing description of why the policy exists. Surfaces in the
    /// admin UI (R0193) and in the audit trail of mutations. Capped at 512 characters
    /// at the EF mapping layer.
    /// </summary>
    public string? Description { get; set; }
}
