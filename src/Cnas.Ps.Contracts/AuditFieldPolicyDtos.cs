namespace Cnas.Ps.Contracts;

/// <summary>
/// R0183 / SEC 043 — single row in the admin-configurable per-entity field-change
/// policy registry. All id fields are Sqid-encoded strings per CLAUDE.md RULE 3;
/// the natural-key <see cref="EntityType"/> remains a raw stable string because
/// operators reference policies by entity type in runbooks and admin URLs.
/// </summary>
/// <param name="Id">Sqid-encoded id of the policy row.</param>
/// <param name="EntityType">
/// CLR short name of the entity this policy governs (e.g. <c>Solicitant</c>).
/// Must match the runtime type's <c>Type.Name</c> exactly — the
/// validator enforces a PascalCase shape so a typo never silently disables
/// tracking.
/// </param>
/// <param name="TrackedFields">
/// Property names whose value changes trigger an audit row + diff entry. Order is
/// preserved across the round-trip.
/// </param>
/// <param name="SuppressedFields">
/// Property names whose values must NEVER appear in the diff payload — when one
/// of these is also tracked, the entry records the change but the value is
/// replaced with <c>"[redacted]"</c>. Overlap with <see cref="TrackedFields"/>
/// is meaningful and documented on the entity.
/// </param>
/// <param name="RequireAnyChange">
/// When <c>true</c>, the diff writer emits an audit row only when at least one
/// tracked field actually differs between the before and after snapshots.
/// </param>
/// <param name="Severity">
/// Stable string form of the <c>AuditSeverity</c> enum (<c>Information</c> |
/// <c>Notice</c> | <c>Sensitive</c> | <c>Critical</c>). The Contracts assembly
/// stays Core-free so we encode severity as a stable string.
/// </param>
/// <param name="IsEnabled">Enable flag distinct from soft-delete.</param>
/// <param name="Description">Free-form admin-facing description; nullable.</param>
public sealed record AuditFieldPolicyOutput(
    string Id,
    string EntityType,
    IReadOnlyList<string> TrackedFields,
    IReadOnlyList<string> SuppressedFields,
    bool RequireAnyChange,
    string Severity,
    bool IsEnabled,
    string? Description);

/// <summary>
/// Request body for <c>POST /api/audit-field-policies</c>. The CRUD surface is
/// restricted to the tech-admin role; mass-assignment protection (per CLAUDE.md
/// §2.4) is enforced by the controller's <c>[Authorize]</c> policy + the absence
/// of any audit / system fields on this input.
/// </summary>
/// <param name="EntityType">CLR short name; validator regex <c>^[A-Z][A-Za-z0-9]{2,63}$</c>.</param>
/// <param name="TrackedFields">Tracked-field list (may be empty when <see cref="RequireAnyChange"/> is false).</param>
/// <param name="SuppressedFields">Suppressed-field list; may be empty.</param>
/// <param name="RequireAnyChange">Defaults to <c>true</c> per SEC 043.</param>
/// <param name="Severity">
/// Stable string form of <c>AuditSeverity</c> (<c>Information</c> | <c>Notice</c>
/// | <c>Sensitive</c> | <c>Critical</c>); required.
/// </param>
/// <param name="IsEnabled">Enable flag; defaults to true.</param>
/// <param name="Description">Optional admin-facing description.</param>
public sealed record AuditFieldPolicyCreateInput(
    string EntityType,
    IReadOnlyList<string> TrackedFields,
    IReadOnlyList<string> SuppressedFields,
    string Severity,
    bool RequireAnyChange = true,
    bool IsEnabled = true,
    string? Description = null);

/// <summary>
/// Request body for <c>PUT /api/audit-field-policies/{sqid}</c>.
/// <c>EntityType</c> is intentionally immutable after create — operator runbooks
/// reference policies by entity type and re-keying a row would break those
/// references. Mutate by disabling the old policy and creating a new one instead.
/// </summary>
/// <param name="TrackedFields">Updated tracked-field list.</param>
/// <param name="SuppressedFields">Updated suppressed-field list.</param>
/// <param name="RequireAnyChange">Updated emission flag.</param>
/// <param name="Severity">Updated severity (stable string form).</param>
/// <param name="IsEnabled">Updated enable flag.</param>
/// <param name="Description">Updated description.</param>
public sealed record AuditFieldPolicyUpdateInput(
    IReadOnlyList<string> TrackedFields,
    IReadOnlyList<string> SuppressedFields,
    bool RequireAnyChange,
    string Severity,
    bool IsEnabled,
    string? Description);
